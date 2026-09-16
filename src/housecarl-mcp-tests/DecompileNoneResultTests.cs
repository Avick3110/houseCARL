using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A call that returns None puts its result in the ::NoneVar discard slot, and the compiler reads
/// that slot back when the call's value is used — `x = obj.VoidCall()` compiles to the call plus a
/// CAST out of ::NoneVar into the destination's type.
///
/// The streams below are what the CK's own PapyrusCompiler emits for the source above each one —
/// checked by compiling that source, and by recompiling the decompiled output back to a .pex with
/// an identical instruction stream. CI has no CK compiler, so the streams are pinned here by hand.
/// </summary>
[Trait("tier", "unit")]
public class DecompileNoneResultTests
{
    [Fact]
    public void AVoidCallWhoseResultIsAssignedReadsAsThatCall()
    {
        // Function AssignVoidResult(HC_NoneTarget f)
        //     Kept = f.Poke()          ; Poke() returns None
        // EndFunction
        // The CAST is the compiler moving the call's None result out of the discard slot; `<none> as
        // HC_NoneTarget` is not writable source, so the bare call is what has to come back.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("::NoneVar"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Kept"), Id("::temp0"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("AssignVoidResult", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("Kept = f.Poke()", res.Source);
    }

    [Fact]
    public void AReturnedVoidCallStaysOneReturnStatement()
    {
        // `return f.Poke()` in a None-returning function: the call's result goes to ::NoneVar and
        // the RETURN reads it back. One statement, not a bare call followed by a bare return.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.RETURN, Id("::NoneVar"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ReturnVoidResult", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("return f.Poke()", res.Source);
    }

    [Fact]
    public void AVoidCallUsedAsAConditionReadsAsThatCall()
    {
        // `if f.Poke()` — the one shape whose read of the slot is a JMPF. A None value is false, so
        // the compiler takes it as a condition and the branch is what it wrote.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.JMPF, Id("::NoneVar"), Int(2));                // -> 3, past the block
        Ins(f, InstructionOpcode.ASSIGN, Id("Flag"), Int(1));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("CondVoid", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("if f.Poke()", res.Source);
    }

    [Fact]
    public void AConditionOnTheSlotThatIsReadAgainInTheBlockFailsLoud()
    {
        // Same shape, but the block reads the slot a second time. The slot is not a value-carrying
        // local — `None` is not a declarable type and `NoneVar` is the compiler's name — so it must
        // never be promoted to one. Loud failure, not a local no compiler would accept.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.JMPF, Id("::NoneVar"), Int(3));                // -> 4, past the block
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("::NoneVar"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Flag"), Id("::temp0"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("CondReuse", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::NoneVar", Assert.Single(res.Failures));
        Assert.DoesNotContain("None NoneVar", res.Source);
    }

    [Fact]
    public void AnInterveningCallKeepsTheVoidCallAtItsOwnPosition()
    {
        // `Poke` runs before `Bar` in the stream. The read of the slot is two instructions away, so
        // taking the call as the read's value would emit it after `Bar` — a silently reordered pair.
        // The call is not taken, and the read that has no value fails loud instead.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("::NoneVar"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Kept"), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("Intervening", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::NoneVar", Assert.Single(res.Failures));
        Assert.DoesNotContain("Kept = f.Poke()", res.Source);
    }

    [Fact]
    public void AVoidCallReturnedAfterAnotherCallKeepsTheirOrder()
    {
        // `First` runs before `Second`. Returning the pending call here would put `Second` ahead of
        // the return that carries `First`.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "Int", "::temp0");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("First"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Second"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.RETURN, Id("::NoneVar"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ReturnAfter", f)));

        var lines = res.Source.Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(lines.FindIndex(l => l.Contains("f.First()")) >= 0
                    && lines.FindIndex(l => l.Contains("f.First()")) < lines.FindIndex(l => l.Contains("f.Second()")));
    }

    [Fact]
    public void TwoDiscardedVoidCallsStayBareStatementsInOrder()
    {
        // Nothing reads the slot, so both calls are plain statements and the second must not
        // overtake the first.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke2"), Id("f"), Id("::NoneVar"), Int(1), Int(7));

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("TwoCalls", f)));

        Assert.Equal(0, res.FunctionsFailed);
        var lines = res.Source.Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(lines.IndexOf("f.Poke()") >= 0 && lines.IndexOf("f.Poke()") < lines.IndexOf("f.Poke2(7)"));
    }

    [Fact]
    public void AVoidCallInsideAGuardedBlockKeepsTheCallAndDropsTheDeadValue()
    {
        // if IntroFX
        //     IntroFX.remove()
        // endif
        // The block's trailing CAST parks the call's None result in the (now dead) condition temp.
        // The call is the only effect in the block, so that is what the block must come back as.
        var f = Fn();
        Local(f, "Bool", "::temp1");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp1"), Id("::IntroFX_var"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp1"), Int(3));                  // -> 4, past the block
        Ins(f, InstructionOpcode.CALLMETHOD, Id("remove"), Id("::IntroFX_var"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CAST, Id("::temp1"), Id("::NoneVar"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("Guarded", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("if IntroFX", res.Source);
        Assert.Contains("IntroFX.remove()", res.Source);
        Assert.DoesNotContain(" as ", res.Source);
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

    static PexObjectVariableData Null()
        => new() { VariableType = VariableType.Null };
}

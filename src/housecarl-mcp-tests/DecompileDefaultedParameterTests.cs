using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A .pex stores no parameter defaults. The compiler bakes an omitted `None` default as a RAW null argument
/// slot at the call site, and the reader re-omits a trailing run of those — so a function declared in the
/// same script came back with neither the default on its signature nor the argument at the call site, and
/// the source no longer compiled. The default is now declared from the same trailing run the call site
/// re-omits, read off the calls this object makes to itself.
///
/// The streams below are what the CK's own PapyrusCompiler emits for the source above each one — checked by
/// compiling that source and by recompiling the decompiled output to a byte-identical stream. CI has no CK
/// compiler, so the streams are pinned here by hand. The last two are deliberately streams it does NOT
/// emit: they pin what a malformed or optimizer-written pex is allowed to do to a signature.
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
    public void TheLongestRunAnyCallShowsIsTheOneDeclared()
    {
        // Function Two(string label, HC_DefProbe f = None, HC_DefProbe g = None)
        // EndFunction
        //     Two("a")          ; omits f and g
        //     Two("b", other)   ; omits g
        // Both call sites have to compile against the one signature, so the longer run is the one declared —
        // the shorter call still compiles against it, the longer one does not compile against the shorter.
        // The shorter run is the LAST call here, so taking whichever came last gives `g = None` only.
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"), ("HC_DefProbe", "g"));
        var caller = Fn(("HC_DefProbe", "other"));
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Two"), Id("self"), Id("::NoneVar"), Int(3), Str("a"), Null(), Null());
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Two"), Id("self"), Id("::NoneVar"), Int(3), Str("b"), Id("other"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Two", target), ("CallTwice", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("HC_DefProbe f = None, HC_DefProbe g = None", res.Source);
    }

    [Fact]
    public void AStaticCallToThisScriptPutsTheDefaultBack()
    {
        // The global form of the same call: `HC_DefProbe.Global("x")`.
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"));
        target.Flags = (FunctionFlags)1;   // Global
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLSTATIC, Str("HC_DefProbe"), Str("Wide"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Wide", target), ("CallWide", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("HC_DefProbe f = None", res.Source);
    }

    [Fact]
    public void ACallOnAnotherObjectLeavesTheSignatureAlone()
    {
        // `other.Ping("x")` — the baked null is a real default, but it is `HC_Other.Ping`'s, not this one's.
        // Declaring it here would give this script an API its author never wrote.
        var target = Fn(("String", "label"), ("HC_Other", "f"));
        var caller = Fn(("HC_Other", "other"));
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Ping"), Id("other"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Ping", target), ("CallOther", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
    }

    [Fact]
    public void AStaticCallToAnotherScriptLeavesTheSignatureAlone()
    {
        // The same thing in the global form: `SomeOtherScript.Ping("x")`.
        var target = Fn(("String", "label"), ("HC_Other", "f"));
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLSTATIC, Str("SomeOtherScript"), Str("Ping"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Ping", target), ("CallStatic", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
    }

    [Fact]
    public void ABakedNullThatIsNotLastLeavesTheSignatureAlone()
    {
        // The call site writes that one out as `None` rather than omitting it, so the source compiles, and a
        // default there would be a defaulted parameter followed by an undefaulted one, which is not legal.
        // Extending the run left would need `int n = 3` too, and a baked `3` is indistinguishable from an
        // argument the author wrote — so that half is not inferable, and this case is out of scope, not
        // settled: what it costs is a `CAST ::temp null` the original did not have.
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
        // A stream the CK compiler does not emit: a call to itself always matches its own parameter count.
        // Taken as if it did, it would default the middle parameter and leave the last one undefaulted.
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"), ("HC_DefProbe", "g"));
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Elsewhere"), Id("self"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Elsewhere", target), ("CallOther", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
    }

    [Fact]
    public void ATrailingParameterNoneIsNotLegalForLeavesTheSignatureAlone()
    {
        // Another stream the CK compiler does not emit: it bakes a scalar default as its own literal, never
        // as a raw null. On a pex some other compiler wrote, this is what keeps `int n = None` out.
        var target = Fn(("String", "label"), ("Int", "n"));
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Ping"), Id("self"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Ping", target), ("CallPing", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
    }

    [Fact]
    public void ACallFromInsideAPropertyHandlerCountsAsEvidence()
    {
        // The only call that omits the argument is in a property's Set handler. Those bodies go through the
        // same re-omission at the call site, so the signature has to learn the default from them too.
        var target = Fn(("HC_DefProbe", "f"));
        var setter = Fn(("Int", "value"));
        Local(setter, "None", "::NoneVar");
        Ins(setter, InstructionOpcode.CALLMETHOD, Id("Refresh"), Id("self"), Id("::NoneVar"), Int(1), Null());

        var pex = File("HC_DefProbe", ("Refresh", target));
        pex.Objects[0].Properties.Add(new PexObjectProperty
        {
            Name = "Level", TypeName = "Int", DocString = "",
            Flags = PropertyFlags.Read | PropertyFlags.Write, WriteHandler = setter,
        });

        var res = PapyrusDecompiler.DecompileFile(pex);

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("HC_DefProbe f = None", res.Source);
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

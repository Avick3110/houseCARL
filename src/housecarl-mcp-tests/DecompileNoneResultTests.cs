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
///
/// The last tests are the ordering rule the slot's route through the pending machinery exposed, and
/// they are here rather than in a file of their own because that route is what found them: a
/// statement that carries a pending value drains only what was produced before that value, and one
/// that has an effect of its own — it runs a call, sets a property, sets an array element — refuses
/// instead, because neither side of it is the source's order.
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

    [Fact]
    public void ACallLandingBetweenTheHopAndItsStatementKeepsTheEarlierCallFirst()
    {
        // The call's value hops into ::temp0 at the next instruction, then an unrelated call lands
        // before the statement that carries it. `Poke` runs before `Bar`, and the statement that
        // carries the older value must not be emitted after the newer one. Both destinations are
        // function locals, so the two stores are invisible to the call held back past them.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "None", "::NoneVar");
        Local(f, "HC_NoneTarget", "Kept");
        Local(f, "Int", "Num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("::NoneVar"));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.ASSIGN, Id("Kept"), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("HopThenIntervening", f)));

        Assert.Equal(0, res.FunctionsFailed);
        AssertOrder(res.Source, "f.Poke()", "f.Bar()");
    }

    [Fact]
    public void TwoInterleavedCallsOnOrdinaryTempsKeepTheirOrder()
    {
        // The same interleave with no discard slot in it at all. The rule is in the pending
        // machinery, not in the ::NoneVar route, so it has to hold here too. Locals again: the
        // member-destination form of this stream is the refusal two tests below.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "HC_NoneTarget", "Kept");
        Local(f, "Int", "Num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.ASSIGN, Id("Kept"), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("Interleave", f)));

        Assert.Equal(0, res.FunctionsFailed);
        AssertOrder(res.Source, "f.Poke()", "f.Bar()");
    }

    [Fact]
    public void AReturnNeverLeavesACallStrandedBehindIt()
    {
        // `Bar` runs in the bytecode. A return ends the region, so a value still pending at it has no
        // later boundary to reach — emitting it after the return would be a call that never runs, and
        // emitting it before would put a later call ahead of the earlier one the return carries.
        // Neither is the source, so the function refuses.
        var f = Fn(("HC_NoneTarget", "f"));
        f.ReturnTypeName = "HC_NoneTarget";
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.RETURN, Id("::temp0"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("RetCarries", f)));

        Assert.Equal(1, res.FunctionsFailed);
        AssertNoStatementAfterReturn(res.Source, "f.Bar()");
    }

    [Fact]
    public void AReturnEndingAnIfArmNeverLeavesACallStrandedBehindIt()
    {
        // The same shape with the return as an if-arm's last instruction: there the stranded call
        // lands past the return from the arm's own end-of-block flush.
        var f = Fn(("bool", "flag"), ("HC_NoneTarget", "f"));
        f.ReturnTypeName = "HC_NoneTarget";
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.JMPF, Id("flag"), Int(4));                     // -> 4, past the arm
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.RETURN, Id("::temp0"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ArmRetCarries", f)));

        Assert.Equal(1, res.FunctionsFailed);
        AssertNoStatementAfterReturn(res.Source, "f.Bar()");
    }

    [Fact]
    public void ACallArgumentNeverOvertakesACallProducedAfterIt()
    {
        // `Poke`, then `Bar`, then `Eat` taking Poke's value. Folding Poke into Eat's argument list runs
        // `Eat` before `Bar`; draining `Bar` first runs it before `Poke`. Neither is the stream's order.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Eat"), Id("f"), Id("::NoneVar"), Int(1), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("CallArg", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("f.Eat(f.Poke())", res.Source);
    }

    [Fact]
    public void APropertySetNeverOvertakesACallProducedAfterIt()
    {
        // Same shape with the carrying statement a property set, which can be a real setter function.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.PROPSET, Str("Prop"), Id("f"), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("PropSet", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("f.Prop = f.Poke()", res.Source);
    }

    [Fact]
    public void AnArrayElementSetNeverOvertakesACallProducedAfterIt()
    {
        // Same shape again, storing into an array — a reference another call can read.
        var f = Fn(("HC_NoneTarget", "f"), ("Int[]", "arr"));
        Local(f, "Int", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.ARRAY_SETELEMENT, Id("arr"), Int(0), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ArraySet", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("arr[0] = f.Poke()", res.Source);
    }

    [Fact]
    public void AWhileBodySetNeverOvertakesACallProducedAfterIt()
    {
        // The same pair inside a while body, where the body's own end-of-block flush is what emitted the
        // held-back call after the store.
        var f = Fn(("bool", "flag"), ("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.JMPF, Id("flag"), Int(5));                      // 0 -> 5, past the loop
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.PROPSET, Str("Prop"), Id("f"), Id("::temp0"));
        Ins(f, InstructionOpcode.JMP, Int(-4));                                  // 4 -> 0, the loop back-jump
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("WhileBody", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("f.Prop = f.Poke()", res.Source);
    }

    [Fact]
    public void AStoreToAScriptMemberNeverOvertakesACallProducedAfterIt()
    {
        // The same interleave, storing into a script member instead of a local. `ReadsCount` ran before
        // the store in the stream, so emitting the store first shows it a value it never saw.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("ReadsCount"), Id("self"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.ASSIGN, Id("Count"), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("Num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("MemberStore", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("Count = f.Poke()", res.Source);
    }

    [Fact]
    public void ACallWritingARealVariableNeverOvertakesACallProducedAfterIt()
    {
        // The call-argument shape with a real destination instead of the discard slot: `Eat` runs here,
        // after `Bar` in the stream, and folding `Poke` into its argument list puts it before `Bar`.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "num");
        Local(f, "Int", "eaten");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Eat"), Id("f"), Id("eaten"), Int(1), Id("::temp0"));
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("CallDest", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("f.Eat(f.Poke())", res.Source);
    }

    [Fact]
    public void AStatementThatOnlyReadsAndStoresToALocalIsNotRefused()
    {
        // The narrowing: this statement folds in `Poke`, which already ran at its own index, and stores
        // into a local. `msg = f.Poke() + "x"` / `num = f.Bar()` runs Poke then Bar — the stream's order.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "String", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "String", "msg");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.STRCAT, Id("msg"), Id("::temp0"), Str("x"));
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ReadOnlyFold", f)));

        Assert.Equal(0, res.FunctionsFailed);
        AssertOrder(res.Source, "f.Poke()", "f.Bar()");
    }

    [Fact]
    public void AStatementFoldingInACallThatRanLaterIsStillRefused()
    {
        // Same statement shape, but the value it folds in runs its own call at index 2 — after `Bar`.
        // `kept = f.Inner(f.Poke())` / `num = f.Bar()` would run Inner before Bar, which the stream did not.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "HC_NoneTarget", "::temp2");
        Local(f, "Int", "kept");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Inner"), Id("f"), Id("::temp2"), Int(1), Id("::temp0"));
        Ins(f, InstructionOpcode.CAST, Id("kept"), Id("::temp2"));
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("NestedFold", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
    }

    [Fact]
    public void AnIfConditionNeverOvertakesACallProducedAfterIt()
    {
        // A condition evaluates and branches. Draining `Bar` ahead of it puts the later call first, and
        // leaving it pending across the branch would consume it inside an arm.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(2));                   // 2 -> 4, past the block
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Side"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("CondCarries", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("if f.Poke()", res.Source);
    }

    [Fact]
    public void AWhileConditionNeverOvertakesACallProducedAfterIt()
    {
        // The same shape with the condition driving a loop.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(3));                   // 2 -> 5, past the loop
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Side"), Id("f"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.JMP, Int(-4));                                  // 4 -> 0, the back-jump
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("WhileCondCarries", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
        Assert.DoesNotContain("while f.Poke()", res.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACallFoldedIntoATempNeverOvertakesACallProducedAfterIt(bool discarded)
    {
        // `Inner` runs at its own index, after `Bar`, and folding `Poke` into its arguments puts it before
        // `Bar`. Nothing downstream consumes the result — it is discarded, or the region ends — so the
        // crossing has to be settled where the call runs and not at a later statement.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "::temp2");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Inner"), Id("f"), Id("::temp2"), Int(1), Id("::temp0"));
        if (discarded) Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("TempFold", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
    }

    [Fact]
    public void AShortCircuitNeverDrainsACallProducedAfterItsLeftOperand()
    {
        // `Bar` runs after `Poke` and its result is discarded, so draining it before the `&&` statement that
        // carries `Poke` puts the later call first — the reorder the plain condition path already refuses.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "Bool", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Bool", "x");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(2));                   // 2 -> 4, the join
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Baz"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.ASSIGN, Id("x"), Id("::temp0"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("ShortCircuit", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
    }

    [Fact]
    public void AWideDrainEmitsInTheOrderTheValuesWereProduced()
    {
        // The add is created after `Bar` but starts where `Poke` does, because that is the value it folded
        // in. It is pure, so it needs no refusal — but it has to come out before `Bar`, not after it.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "Int", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "::temp2");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.IADD, Id("::temp2"), Id("::temp0"), Int(1));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("WideOrder", f)));

        Assert.Equal(0, res.FunctionsFailed);
        AssertOrder(res.Source, "f.Poke() + 1", "f.Bar()");
    }

    [Fact]
    public void APropertyReadNeverOvertakesACallProducedAfterIt()
    {
        // A property get can be a real `Function Get()`, the same way a set can be a real setter, so the
        // read runs at its own index — after `Bar` — and must not be emitted before it.
        var f = Fn(("HC_NoneTarget", "f"));
        Local(f, "HC_NoneTarget", "::temp0");
        Local(f, "Int", "::temp1");
        Local(f, "Int", "::temp2");
        Local(f, "Int", "x");
        Local(f, "Int", "num");
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Poke"), Id("f"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Bar"), Id("f"), Id("::temp1"), Int(0));
        Ins(f, InstructionOpcode.PROPGET, Id("Prop"), Id("::temp0"), Id("::temp2"));
        Ins(f, InstructionOpcode.ASSIGN, Id("x"), Id("::temp2"));
        Ins(f, InstructionOpcode.ASSIGN, Id("num"), Id("::temp1"));
        Ins(f, InstructionOpcode.RETURN, Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_NoneProbe", ("PropGetFold", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp1", Assert.Single(res.Failures));
    }

    /// <summary>No emitted line carries <paramref name="stranded"/> after a `return` in the same block.</summary>
    static void AssertNoStatementAfterReturn(string source, string stranded)
    {
        var lines = source.Split('\n').Select(l => l.Trim()).ToList();
        for (int k = 0; k < lines.Count; k++)
            if (lines[k].StartsWith("return") && lines.Skip(k + 1).TakeWhile(l => l != "endif" && l != "EndFunction").Any(l => l.Contains(stranded)))
                Assert.Fail($"'{stranded}' is emitted after a return, where it never runs:\n{source}");
    }

    static void AssertOrder(string source, string first, string second)
    {
        var lines = source.Split('\n').Select(l => l.Trim()).ToList();
        int a = lines.FindIndex(l => l.Contains(first));
        int b = lines.FindIndex(l => l.Contains(second));
        Assert.True(a >= 0 && b >= 0 && a < b, $"expected '{first}' before '{second}' in:\n{source}");
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

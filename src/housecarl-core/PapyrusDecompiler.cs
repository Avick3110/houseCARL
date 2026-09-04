using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

/// <summary>
/// PEX -> Papyrus source reconstruction over Mutagen's PexFile model.
/// The codegen patterns this relies on, each confirmed against compiler output:
///   - jump offsets relative to the jump instruction itself
///   - while  = cond; JMPF -> E; body; JMP (backward); E:
///   - if     = cond; JMPF -> L; then; JMP -> M; L: else; M:   (JMP -> L means no else)
///   - and/or = short-circuit JMPF/JMPT landing ON the final consuming conditional jump, same temp
///   - auto-prop backing var ::Name_var; AutoReadOnly = GET returning a literal
///   - compiler-generated GotoState/GetState in the '' state (skipped on emit)
///   - FunctionFlags raw bits: bit0 = Global, bit1 = Native (Mutagen's enum names sit one off)
/// Any function whose flow doesn't match a verified pattern FAILS LOUD and is counted — never
/// silently emitted wrong.
/// </summary>
public sealed class PapyrusDecompiler
{
    public sealed class Result
    {
        public string Source = "";
        public int FunctionsTotal;
        public int FunctionsFailed;
        public List<string> Failures = new();

        /// <summary>Count of flow patterns the canonical CK compiler never emits (threaded shared-join
        /// trailing JMPs, jump-to-end early returns, value-reused temps):
        /// &gt;0 means the pex came from an OPTIMIZING compiler (Caprica class). The decompiled source
        /// is still correct, but recompiling it with the CK compiler will not reproduce the original
        /// bytes — the optimizer's output is not the CK compiler's canonical form.</summary>
        public int OptimizerHints;
    }

    sealed class StructureException : Exception
    {
        public StructureException(string msg) : base(msg) { }
    }

    // ------------------------------------------------------------------ expression tree
    abstract record Expr;
    sealed record EConst(string Text) : Expr;
    sealed record EIdent(string Name) : Expr;
    sealed record EBin(string Op, Expr L, Expr R) : Expr;
    sealed record EUn(string Op, Expr E) : Expr;
    sealed record ECast(Expr E, string Type) : Expr;
    sealed record ECall(Expr? Target, string Name, List<Expr> Args) : Expr;          // Target null => self
    sealed record EStatic(string Cls, string Name, List<Expr> Args) : Expr;
    sealed record EParent(string Name, List<Expr> Args) : Expr;
    sealed record EProp(Expr? Obj, string Name) : Expr;                              // Obj null => self
    sealed record EIndex(Expr Arr, Expr Idx) : Expr;
    sealed record ELen(Expr Arr) : Expr;
    sealed record ENew(string ElemType, Expr Size) : Expr;
    sealed record EFind(Expr Arr, Expr Val, Expr Start, bool Reverse) : Expr;

    static int Prec(Expr e) => e switch
    {
        EBin b => b.Op switch
        {
            "||" => 1, "&&" => 2,
            "==" or "!=" => 3,
            "<" or ">" or "<=" or ">=" => 4,
            "+" or "-" => 5,
            "*" or "/" or "%" => 6,
            _ => 5,
        },
        EUn => 7,
        ECast => 9,   // renders fully parenthesized — atomic to any parent
        _ => 9,
    };

    static string Render(Expr e) => e switch
    {
        EConst c => c.Text,
        EIdent i => i.Name,
        EBin b => $"{Wrap(b.L, Prec(b))}{(true ? " " : "")}{b.Op} {Wrap(b.R, Prec(b) + 1)}",
        EUn u => u.Op + Wrap(u.E, 7),
        // `as` binds tighter than binary operators — a binop operand must keep its own parens or
        // `(a || b as float)` regroups to `a || (b as float)`.
        ECast c => $"({(c.E is EBin ? "(" + Render(c.E) + ")" : Render(c.E))} as {c.Type})",
        ECall c => (c.Target is null ? "" : Postfix(c.Target) + ".") + c.Name + "(" + string.Join(", ", c.Args.Select(Render)) + ")",
        EStatic s => $"{s.Cls}.{s.Name}(" + string.Join(", ", s.Args.Select(Render)) + ")",
        EParent p => $"Parent.{p.Name}(" + string.Join(", ", p.Args.Select(Render)) + ")",
        // Self-property access renders with the explicit Self. prefix: inside the defining script a
        // BARE auto-property name compiles directly to the backing var (no PROPGET) while Self.Name
        // forces the PROPGET — the bytecode we decompiled showed a PROPGET, so Self. reproduces it.
        // (For full/inherited properties both forms compile to PROPGET — Self. is always faithful.)
        EProp p => p.Obj is null ? $"Self.{p.Name}" : $"{Postfix(p.Obj)}.{p.Name}",
        EIndex x => $"{Postfix(x.Arr)}[{Render(x.Idx)}]",
        ELen l => $"{Postfix(l.Arr)}.Length",
        ENew n => $"new {n.ElemType}[{Render(n.Size)}]",
        EFind f => $"{Postfix(f.Arr)}.{(f.Reverse ? "RFind" : "Find")}({Render(f.Val)}" +
                   (IsDefaultStart(f) ? ")" : $", {Render(f.Start)})"),
        _ => throw new StructureException($"unrenderable expr {e.GetType().Name}"),
    };

    static bool IsDefaultStart(EFind f) =>
        f.Start is EConst c && c.Text == (f.Reverse ? "-1" : "0");

    static string Wrap(Expr e, int minPrec) => Prec(e) < minPrec ? "(" + Render(e) + ")" : Render(e);

    /// <summary>Postfix positions (member access, indexing) need parens around computed bases.</summary>
    static string Postfix(Expr e) => e is EBin or EUn ? "(" + Render(e) + ")" : Render(e);

    // ------------------------------------------------------------------ state
    readonly PexFile _pex;
    readonly PexObject _obj;
    readonly Result _res = new();
    readonly StringBuilder _sb = new();
    Dictionary<string, string> _localTypes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _objVarTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional child→parent class map (from `ScriptName X extends Y` headers across the
    /// load order + vanilla sources). Enables implicit-UPCAST detection: a CAST to an ancestor type
    /// is implicit in every Papyrus context, and re-emitting it explicitly changes codegen: implicit
    /// arg conversions compile as a batched eval-all-then-convert-all pass, while explicit casts
    /// compile inline per-arg. Injected per call rather than held in a static, so parallel use is
    /// safe. Null means no map, and unknown hierarchies keep their explicit casts — conservative and
    /// still correct, only cosmetically different.</summary>
    readonly IReadOnlyDictionary<string, string>? _classParents;

    public PapyrusDecompiler(PexFile pex, PexObject obj, IReadOnlyDictionary<string, string>? classParents = null)
    {
        _pex = pex; _obj = obj; _classParents = classParents;
        // Object variables (incl. auto-prop backing vars) — TypeOf needs them for implicit-cast
        // detection on casts whose source is a script variable, not a function local.
        foreach (var v in obj.Variables)
            if (v.Name is not null && v.TypeName is not null) _objVarTypes.TryAdd(v.Name, v.TypeName);
    }

    public static Result DecompileFile(PexFile pex, IReadOnlyDictionary<string, string>? classParents = null)
    {
        var total = new Result();
        var sb = new StringBuilder();
        foreach (var obj in pex.Objects)
        {
            var d = new PapyrusDecompiler(pex, (PexObject)obj, classParents);
            var r = d.Emit();
            sb.Append(r.Source);
            total.FunctionsTotal += r.FunctionsTotal;
            total.FunctionsFailed += r.FunctionsFailed;
            total.Failures.AddRange(r.Failures);
            total.OptimizerHints += r.OptimizerHints;
        }
        total.Source = sb.ToString();
        return total;
    }

    // ------------------------------------------------------------------ object emission
    public Result Emit()
    {
        var flags = ObjFlags(_obj.RawUserFlags);
        _sb.Append($"ScriptName {_obj.Name}");
        if (!string.IsNullOrEmpty(_obj.ParentClassName)) _sb.Append($" extends {_obj.ParentClassName}");
        if (flags.Length > 0) _sb.Append(' ').Append(flags);
        _sb.AppendLine();
        Doc(_obj.DocString, "");
        _sb.AppendLine();

        // Variables (skip compiler-generated :: names — auto-prop backing vars re-emerge as properties).
        foreach (var v in _obj.Variables.Where(v => !v.Name.StartsWith("::")))
        {
            _sb.Append($"{TypeName(v.TypeName)} {v.Name}");
            var init = InitText(v.VariableData);
            if (init is not null) _sb.Append($" = {init}");
            var vf = ObjFlags(v.RawUserFlags);
            if (vf.Length > 0) _sb.Append(' ').Append(vf);
            _sb.AppendLine();
        }
        if (_obj.Variables.Any(v => !v.Name.StartsWith("::"))) _sb.AppendLine();

        foreach (var p in _obj.Properties) EmitProperty(p);

        // The '' state = top level; named states after. Order functions by debug-info line where known.
        foreach (var st in _obj.States.OrderBy(s => string.IsNullOrEmpty(s.Name) ? 0 : 1))
        {
            var named = !string.IsNullOrEmpty(st.Name);
            if (named)
            {
                var auto = string.Equals(st.Name, _obj.AutoStateName, StringComparison.OrdinalIgnoreCase);
                _sb.AppendLine($"{(auto ? "Auto " : "")}State {st.Name}");
            }
            var ind = named ? "    " : "";
            foreach (var f in OrderBySourceLine(st))
            {
                if (!named && IsCompilerGenerated(f)) continue;
                EmitFunction(f.FunctionName, f.Function, ind, st.Name);
            }
            if (named) _sb.AppendLine("EndState").AppendLine();
        }

        _res.Source = _sb.ToString();
        return _res;
    }

    IEnumerable<PexObjectNamedFunction> OrderBySourceLine(PexObjectState st)
        => st.Functions.Cast<PexObjectNamedFunction>().OrderBy(f =>
        {
            var dbg = _pex.DebugInfo?.Functions.FirstOrDefault(d =>
                string.Equals(d.ObjectName, _obj.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.StateName, st.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.FunctionName, f.FunctionName, StringComparison.OrdinalIgnoreCase));
            return dbg is not null && dbg.Instructions.Count > 0 ? dbg.Instructions.Min(x => (int)x) : int.MaxValue;
        });

    bool IsCompilerGenerated(PexObjectNamedFunction f)
    {
        if (string.Equals(f.FunctionName, "GetState", StringComparison.OrdinalIgnoreCase))
            return f.Function.Instructions.Count == 1
                && f.Function.Instructions[0].OpCode == InstructionOpcode.RETURN;
        if (string.Equals(f.FunctionName, "GotoState", StringComparison.OrdinalIgnoreCase))
            return f.Function.Instructions.Count == 3
                && f.Function.Instructions[1].OpCode == InstructionOpcode.ASSIGN;
        return false;
    }

    string ObjFlags(uint raw)
    {
        var parts = new List<string>();
        for (int bit = 0; bit < 32 && bit < _pex.UserFlags.Length; bit++)
            if ((raw & (1u << bit)) != 0 && !string.IsNullOrEmpty(_pex.UserFlags[bit]))
                parts.Add(char.ToUpperInvariant(_pex.UserFlags[bit][0]) + _pex.UserFlags[bit][1..]);
        return string.Join(" ", parts);
    }

    void Doc(string? doc, string ind)
    {
        if (string.IsNullOrEmpty(doc)) return;
        _sb.AppendLine($"{ind}{{{doc}}}");
    }

    void EmitProperty(PexObjectProperty p)
    {
        var t = TypeName(p.TypeName);
        var hasAuto = p.Flags.HasFlag(PropertyFlags.AutoVar);
        var flagsTxt = ObjFlags(p.RawUserFlags);
        var suffix = flagsTxt.Length > 0 ? " " + flagsTxt : "";
        if (hasAuto)
        {
            var backing = _obj.Variables.FirstOrDefault(v => string.Equals(v.Name, p.AutoVarName, StringComparison.OrdinalIgnoreCase));
            var init = backing is not null ? InitText(backing.VariableData) : null;
            // Conditional on an auto property lands on the BACKING VARIABLE's user flags, not the
            // property's — merge both so `Auto Conditional` survives the round trip.
            var autoFlagsTxt = ObjFlags(p.RawUserFlags | (backing?.RawUserFlags ?? 0));
            var autoSuffix = autoFlagsTxt.Length > 0 ? " " + autoFlagsTxt : "";
            _sb.AppendLine($"{t} Property {p.Name}{(init is not null ? $" = {init}" : "")} Auto{autoSuffix}");
            Doc(p.DocString, "");
        }
        else if (p.ReadHandler is not null && p.WriteHandler is null
                 && p.ReadHandler.Instructions.Count == 1
                 && p.ReadHandler.Instructions[0].OpCode == InstructionOpcode.RETURN
                 && p.ReadHandler.Instructions[0].Arguments.Count == 1
                 && p.ReadHandler.Instructions[0].Arguments[0].VariableType != VariableType.Identifier)
        {
            var lit = InitText(p.ReadHandler.Instructions[0].Arguments[0]);
            _sb.AppendLine($"{t} Property {p.Name} = {lit} AutoReadOnly{suffix}");
            Doc(p.DocString, "");
        }
        else
        {
            _sb.AppendLine($"{t} Property {p.Name}{suffix}");
            Doc(p.DocString, "    ");
            if (p.ReadHandler is not null) EmitFunction("Get", p.ReadHandler, "    ", propertyHandler: true);
            if (p.WriteHandler is not null) EmitFunction("Set", p.WriteHandler, "    ", propertyHandler: true);
            _sb.AppendLine("EndProperty");
        }
        _sb.AppendLine();
    }

    // ------------------------------------------------------------------ function emission
    void EmitFunction(string name, PexObjectFunction f, string ind, string state = "", bool propertyHandler = false)
    {
        _res.FunctionsTotal++;
        var raw = (uint)f.Flags;
        bool isGlobal = (raw & 1) != 0, isNative = (raw & 2) != 0;
        var ret = string.IsNullOrEmpty(f.ReturnTypeName) || f.ReturnTypeName.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? null : TypeName(f.ReturnTypeName);
        bool asEvent = !propertyHandler && ret is null && !isGlobal && name.StartsWith("On", StringComparison.OrdinalIgnoreCase);

        var ps = string.Join(", ", f.Parameters.Select(p => $"{TypeName(p.TypeName)} {p.Name}"));
        var kw = asEvent ? "Event" : "Function";
        var header = $"{ind}{(ret is not null ? ret + " " : "")}{kw} {name}({ps})"
                   + (isGlobal ? " Global" : "") + (isNative ? " Native" : "");

        _localTypes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in f.Parameters) _localTypes[p.Name] = p.TypeName;
        foreach (var l in f.Locals) _localTypes[l.Name] = l.TypeName;

        _sb.AppendLine(header);
        Doc(f.DocString, ind + "    ");
        if (isNative) return;   // native: declaration only

        // Structure FIRST: materialized temps (optimizer value-reuse promoted to named locals)
        // are only known after the walk, and their declarations belong with the other locals.
        List<string>? stmts = null;
        StructureException? fail = null;
        var body = new Body(this, f);
        try { stmts = body.Structure(0, f.Instructions.Count); }
        catch (StructureException ex) { fail = ex; }

        // Locals: declare at the first assignment when scope-safe (`int i = 0` form). PCompiler
        // allocates the locals-table slot where it SEES the declaration, and the table order drives
        // its same-type temp-slot reuse picks — hoisting everything to the top reorders the table
        // versus idiomatically-authored sources and shows up as temp-slot reallocations on recompile.
        // A local stays hoisted when inline placement is NOT provably safe: first reference isn't a
        // write, the initializer references the local itself, or a reference escapes the block the
        // first write sits in (Papyrus locals are block-scoped).
        var declarables = f.Locals
            .Where(l => !IsTemp(l.Name!) && !l.TypeName!.Equals("None", StringComparison.OrdinalIgnoreCase))
            .Concat(f.Locals.Where(l => body.Materialized.Contains(l.Name!)))
            .ToList();
        var placed = fail is null ? PlaceDeclsAtFirstAssign(stmts!, declarables) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in declarables.Where(l => !placed.Contains(l.Name!)))
            _sb.AppendLine($"{ind}    {TypeName(l.TypeName!)} {LhsName(l.Name!)}");

        if (fail is null)
        {
            foreach (var line in stmts!) _sb.AppendLine(ind + "    " + line);
        }
        else
        {
            _res.FunctionsFailed++;
            _res.Failures.Add($"{_obj.Name}{(state.Length > 0 ? $".{state}" : "")}.{name}: {fail.Message}");
            _sb.AppendLine($"{ind}    ; !!! DECOMPILE FAILED ({fail.Message}) — raw bytecode:");
            foreach (var ins in f.Instructions)
                _sb.AppendLine($"{ind}    ;   {ins.OpCode} {string.Join(" ", ins.Arguments.Select(RawVal))}");
        }

        _sb.AppendLine($"{ind}End{kw}");
        if (!propertyHandler) _sb.AppendLine();
    }

    /// <summary>Raw instruction-argument render for the loud-failure bytecode dump.</summary>
    static string RawVal(IPexObjectVariableDataGetter? d) => d is null ? "(none)" : d.VariableType switch
    {
        VariableType.Null => "null",
        VariableType.Identifier => $"id:{d.StringValue}",
        VariableType.String => $"\"{d.StringValue}\"",
        VariableType.Integer => $"int:{d.IntValue}",
        VariableType.Float => $"flt:{d.FloatValue}",
        VariableType.Bool => $"bool:{d.BoolValue}",
        _ => $"?{d.VariableType}",
    };

    /// <summary>Rewrite each local's first-assignment line to a declaration-with-initializer when
    /// every other reference stays inside the block that assignment sits in. Returns the set of
    /// local names (table names, e.g. "::temp140") that were placed inline.</summary>
    HashSet<string> PlaceDeclsAtFirstAssign(List<string> stmts, List<PexObjectFunctionVariable> declarables)
    {
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in declarables)
        {
            var name = LhsName(l.Name!);
            var word = new System.Text.RegularExpressions.Regex(
                $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var refs = new List<int>();
            for (int k = 0; k < stmts.Count; k++)
                if (word.IsMatch(stmts[k])) refs.Add(k);
            if (refs.Count == 0) continue;                       // unreferenced: keep hoisted, to preserve the locals-table order

            int first = refs[0];
            var line = stmts[first];
            int indent = line.Length - line.TrimStart(' ').Length;
            var body = line[indent..];
            // First reference must be a plain write whose RHS does not read the local itself.
            if (!body.StartsWith(name + " = ", StringComparison.OrdinalIgnoreCase)) continue;
            var rhs = body[(name.Length + 3)..];
            if (word.IsMatch(rhs)) continue;
            // Block containment: the write's block runs until the first line that dedents below it
            // (else/elseif/endif/endwhile of the enclosing construct dedent by one level).
            int blockEnd = stmts.Count;
            for (int k = first + 1; k < stmts.Count; k++)
            {
                int ki = stmts[k].Length - stmts[k].TrimStart(' ').Length;
                if (ki < indent) { blockEnd = k; break; }
            }
            if (refs.Any(r => r > first && r >= blockEnd)) continue;

            stmts[first] = $"{new string(' ', indent)}{TypeName(l.TypeName!)} {body}";
            placed.Add(l.Name!);
        }
        return placed;
    }

    // ------------------------------------------------------------------ body structuring
    sealed class Body
    {
        readonly PapyrusDecompiler _d;
        readonly PexObjectFunction _f;
        readonly List<PexObjectFunctionInstruction> _ins;
        readonly Dictionary<string, Expr> _pending = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _pendingStart = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _pendingOrder = new();
        int _consumedStart;   // min start-index of pending values consumed while decoding the current instruction
        int _cur;             // index of the instruction currently being decoded (diagnostics)

        /// <summary>Temps promoted to named locals: an optimizing compiler can condition on a temp
        /// and then RE-READ its value inside the guarded block — PCompiler temps
        /// are strictly single-use, so re-use forces the temp to become a real local in the source.
        /// Declared at function top (function scope is always valid); reads/writes emit by name.</summary>
        public readonly HashSet<string> Materialized = new(StringComparer.OrdinalIgnoreCase);

        void SetPending(string name, Expr e, List<string> stmts, int startIdx)
        {
            // Overwriting an unconsumed pending means the earlier value was discarded — a statement in
            // the original source. Calls are a bare-call statement; EBin is a bare expression statement
            // (the author wrote `x + y` with no assignment, which PCompiler compiles as eval-into-temp).
            // Anything else stays a loud failure.
            if (_pending.TryGetValue(name, out var old))
            {
                if (IsCallish(old) || old is EBin) stmts.Add(Render(old));
                else throw new StructureException($"pending non-statement value on {name} overwritten ({old.GetType().Name})");
                DropPending(name);
            }
            _pending[name] = e;
            _pendingStart[name] = startIdx;
            _pendingOrder.Add(name);
        }

        void DropPending(string name)
        {
            _pending.Remove(name);
            _pendingStart.Remove(name);
            _pendingOrder.Remove(name);
        }

        /// <summary>Unconsumed values pending at a statement boundary. A pending temp whose value is
        /// READ downstream (before being rewritten) is an optimizer-eliminated named local crossing a
        /// region boundary (value flows into an if arm, or out of an arm to the join — a phi):
        /// materialize it as a named-local assignment, never discard it. The rest are discarded
        /// results from earlier statements — emit in evaluation order: calls are bare-call statements;
        /// EBin is a bare expression statement (e.g. `prop + "…"` with no assignment, which PCompiler
        /// accepts and compiles). Other expression kinds (EProp, EIndex, …) are NOT known to
        /// round-trip — a bare variable read compiles to nothing — so they stay a loud failure.</summary>
        void FlushPending(List<string> stmts) => FlushPending(stmts, _cur + 1);

        void FlushPending(List<string> stmts, int scanFrom)
        {
            foreach (var name in _pendingOrder.ToList())
            {
                var e = _pending[name];
                if (IsTemp(name) && !name.Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
                    && ReadsBeforeWrite(scanFrom, _ins.Count, name))
                {
                    Materialized.Add(name);
                    _d._res.OptimizerHints++;   // value-reused temp: PCompiler temps are strictly single-use
                    stmts.Add($"{LhsName(name)} = {Render(e)}");
                    DropPending(name);
                    continue;
                }
                if (!IsCallish(e) && e is not EBin)
                    throw new StructureException($"leftover non-statement pending temp {name} ({e.GetType().Name})");
                stmts.Add(Render(e));
                DropPending(name);
            }
        }

        /// <summary>Flush ONLY pending discarded-result calls (statements by construction), leaving
        /// expression values in place. Used at a short-circuit boundary: pre-if bare-call statements
        /// must drain BEFORE the arm is structured (their temps may be reused inside the arm), but
        /// the condition's own pending values must survive into the combined expression.</summary>
        void FlushPendingCalls(List<string> stmts)
        {
            foreach (var name in _pendingOrder.ToList())
            {
                if (!IsCallish(_pending[name])) continue;
                stmts.Add(Render(_pending[name]));
                DropPending(name);
            }
        }

        static bool IsCallish(Expr e) => e is ECall or EStatic or EParent;

        public Body(PapyrusDecompiler d, PexObjectFunction f)
        {
            _d = d; _f = f;
            _ins = f.Instructions.Cast<PexObjectFunctionInstruction>().ToList();
        }

        // Function-level region: jumping to one-past-the-last-instruction ends the function
        // (implicit default return) — exit-equivalent by definition.
        public List<string> Structure(int lo, int hi) => Structure(lo, hi, flushAtEnd: true, exits: new HashSet<int> { hi }, cont: hi);

        /// <summary>flushAtEnd=false for short-circuit expression arms — their pending values must
        /// survive into the enclosing condition; a trailing flush would misemit them as statements.
        /// <paramref name="exits"/>: instruction indices provably equivalent to "fall off the end of
        /// this region" (the region's own trailing-JMP slot, the enclosing if's join, and — when this
        /// region's join IS the parent's end — the parent's exits, recursively). An optimizer
        /// (a jump-threading class) collapses a jump-to-a-trailing-JMP into a direct jump to the
        /// shared join, so nested constructs may target an ENCLOSING join instead of their own;
        /// jumping to any index in <paramref name="exits"/> is identical to falling out of the region.
        /// Jumps beyond hi that are NOT exit-equivalent stay loud failures.
        /// <paramref name="cont"/>: where control RESUMES after falling off this region (the join for
        /// if arms, the condition start for while bodies, hi at function level) — the region-end
        /// flush scans from there for downstream reads; the next linear index would wrongly scan a
        /// sibling arm the flow never reaches.</summary>
        List<string> Structure(int lo, int hi, bool flushAtEnd, HashSet<int> exits, int cont)
        {
            var stmts = new List<string>();
            int i = lo;
            while (i < hi)
            {
                var ins = _ins[i];
                var op = ins.OpCode;
                var a = ins.Arguments;
                _consumedStart = int.MaxValue;
                _cur = i;

                switch (op)
                {
                    case InstructionOpcode.NOP:
                        i++; break;

                    case InstructionOpcode.JMP:
                    {
                        int t = i + IntArg(a[0]);
                        // Jump to the next instruction: a structural no-op wherever it appears.
                        if (t == i + 1) { i++; break; }
                        // A trailing JMP whose join was claimed by an ENCLOSING construct (threaded
                        // shared-join shape): jump to an exit-equivalent index in last position is a
                        // no-op — identical to falling off the region end.
                        if (exits.Contains(t) && i == hi - 1) { _d._res.OptimizerHints++; i++; break; }
                        // Dead jump: a JMP immediately after a return statement is unreachable
                        // (canonical then-end filler the else-claim usually consumes; threading can
                        // leave it dangling, even pointed past the function end). Skipping it cannot
                        // change behavior.
                        if (stmts.Count > 0 && (stmts[^1] == "return" || stmts[^1].StartsWith("return ")))
                        { i++; break; }
                        // A reachable jump to one-past-the-last-instruction in a None-returning
                        // function IS a return statement (the VM's fall-off returns None) — the
                        // optimizer canonicalizes early `Return` into a jump to function end.
                        // Value-returning functions stay loud (fall-off default-value semantics
                        // unverified).
                        if (t == _ins.Count && ReturnsNone())
                        {
                            _d._res.OptimizerHints++;
                            FlushPending(stmts);
                            stmts.Add("return");
                            i++; break;
                        }
                        throw new StructureException($"unmatched JMP @{i} -> {t} (unknown flow pattern)");
                    }

                    case InstructionOpcode.JMPF:
                    case InstructionOpcode.JMPT:
                    {
                        var condName = a[0].VariableType == VariableType.Identifier ? IdName(a[0]) : null;
                        int target = i + IntArg(a[1]);
                        if (target <= i) throw new StructureException($"backward conditional jump @{i}");
                        if (target > hi)
                        {
                            // Jump-threaded false-path: targets an enclosing join instead of this
                            // region's end. Exit-equivalent ⇒ clamp to the region end; else fail loud.
                            if (!exits.Contains(target))
                                throw new StructureException($"conditional jump @{i} -> {target} escapes region end {hi}");
                            target = hi;
                        }

                        // Short-circuit: the jump lands ON the instruction that consumes the temp as a
                        // source — another conditional jump (plain &&/||), a CAST hop into a different
                        // temp (nested mixed-temp conditions), an ASSIGN/RETURN/call-arg (`x = a || b`).
                        // Temps only: a real-var condition is always a plain if.
                        if (condName is not null && IsTemp(condName) && target < hi
                            && ConsumesAsSource(_ins[target], condName))
                        {
                            var (left, leftStart) = Consume(condName, i);
                            // Pre-if discarded-result calls may still pend here (their temps can be
                            // reused INSIDE the arm — reuse would misemit them as arm statements).
                            // They are statements that precede the if: drain them now, in order.
                            FlushPendingCalls(stmts);
                            // Evaluate the right side (cur+1 .. target) — must produce only pending values.
                            var sub = Structure(i + 1, target, flushAtEnd: false, exits: new HashSet<int>(), cont: target);
                            if (sub.Count > 0) throw new StructureException($"short-circuit arm @{i + 1}..{target} produced statements");
                            var (right, _) = Consume(condName, target);
                            var combined = new EBin(op == InstructionOpcode.JMPF ? "&&" : "||", left, right);
                            SetPending(condName, combined, stmts, leftStart);
                            i = target;
                            continue;
                        }

                        Expr cond; int condStart;
                        if (condName is not null) (cond, condStart) = Consume(condName, i);
                        else { cond = Resolve(a[0]); condStart = i; }

                        bool isWhile = target - 1 > i && target - 1 < hi
                            && _ins[target - 1].OpCode == InstructionOpcode.JMP
                            && (target - 1) + IntArg(_ins[target - 1].Arguments[0]) <= i;

                        // Drain other pendings BEFORE the condition temp materializes — they predate
                        // the condition in the stream (chronological statement order; FlushPending
                        // itself materializes any whose value flows into the arms).
                        FlushPending(stmts);

                        // Optimizer-reused condition temp: the temp's VALUE is read again inside the
                        // guarded block (PCompiler temps are single-use — this only fires on optimized
                        // codegen). Promote the temp to a named local: assign it here, condition on
                        // the name, and let later reads/writes use the name. NEVER for a while — the
                        // loop re-evaluates its condition, hoisting the assignment would change
                        // semantics (a re-read inside a while body stays a loud failure).
                        if (!isWhile && condName is not null && IsTemp(condName)
                            && ReadsBeforeWrite(i + 1, target, condName))
                        {
                            Materialized.Add(condName);
                            _d._res.OptimizerHints++;   // value-reused condition temp
                            stmts.Add($"{LhsName(condName)} = {Render(cond)}");
                            cond = new EIdent(LhsName(condName));
                        }

                        // Statement-level JMPT is an inverted branch: the same if/while shapes with the
                        // condition negated. The CK compiler ALWAYS emits statement conditionals as
                        // JMPF — its JMPTs live only inside short-circuit arms, consumed above — so
                        // reaching here on a JMPT means the pex came from an optimizing compiler.
                        if (op == InstructionOpcode.JMPT)
                        {
                            _d._res.OptimizerHints++;
                            cond = cond is EUn { Op: "!" } un ? un.E : new EUn("!", cond);
                        }

                        // while: target-1 is a backward JMP to the condition start.
                        if (isWhile)
                        {
                            int back = (target - 1) + IntArg(_ins[target - 1].Arguments[0]);
                            if (back != condStart)
                                throw new StructureException($"while back-jump @{target - 1} -> {back}, expected cond start {condStart}");
                            // Falling off the body ≡ the back JMP ≡ a direct jump to the cond start.
                            var bodyStmts = Structure(i + 1, target - 1, flushAtEnd: true,
                                exits: new HashSet<int> { target - 1, condStart }, cont: condStart);
                            stmts.Add($"while {Render(cond)}");
                            stmts.AddRange(bodyStmts.Select(s => "    " + s));
                            stmts.Add("endwhile");
                            i = target;
                            continue;
                        }

                        // if / if-else: then-block ends with a forward JMP (to endif) or falls through.
                        int elseLo = target, elseHi = target;
                        int thenHi = target;
                        if (target - 1 > i && target - 1 < hi
                            && _ins[target - 1].OpCode == InstructionOpcode.JMP)
                        {
                            int m = (target - 1) + IntArg(_ins[target - 1].Arguments[0]);
                            // m beyond hi is legal when exit-equivalent (threaded then-end jump).
                            // Claim validation: threaded code can aim a nested if's false-path INTO
                            // the would-be else range [target, m) — a tree-shaped else can't be
                            // entered from the then side, so such a claim would mis-structure; read
                            // the if as no-else instead (the range is then plain fall-through code).
                            if (m >= target && (m <= hi || exits.Contains(m))
                                && !AnyJumpInto(i + 1, target - 1, target, m))
                            { thenHi = target - 1; elseHi = Math.Min(m, hi); }
                        }
                        // Child regions inherit: their own join slots, plus the parent's exits when
                        // this if's join IS the parent's end (join chains collapse under threading).
                        var thenExits = new HashSet<int> { thenHi, elseHi };
                        var elseExits = new HashSet<int> { elseHi };
                        if (elseHi == hi) { thenExits.UnionWith(exits); elseExits.UnionWith(exits); }
                        // Both arms resume at the join — region-end value flow scans from there.
                        var thenStmts = Structure(i + 1, thenHi, flushAtEnd: true, exits: thenExits, cont: elseHi);
                        var elseStmts = elseHi > elseLo ? Structure(elseLo, elseHi, flushAtEnd: true, exits: elseExits, cont: elseHi) : new List<string>();

                        stmts.Add($"if {Render(cond)}");
                        stmts.AddRange(thenStmts.Select(s => "    " + s));
                        // elseif collapse: else-block that is exactly one if..endif.
                        if (elseStmts.Count > 0)
                        {
                            if (elseStmts[0].StartsWith("if ") && elseStmts[^1] == "endif"
                                && BlockIsSingleIf(elseStmts))
                            {
                                stmts.Add("else" + elseStmts[0]);                       // "elseif <cond>"
                                stmts.AddRange(elseStmts.Skip(1).Take(elseStmts.Count - 2));
                                stmts.Add("endif");
                            }
                            else
                            {
                                stmts.Add("else");
                                stmts.AddRange(elseStmts.Select(s => "    " + s));
                                stmts.Add("endif");
                            }
                        }
                        else stmts.Add("endif");

                        i = elseHi;
                        continue;
                    }

                    case InstructionOpcode.RETURN:
                    {
                        var v = a[0];
                        // `return <NoneCall>()` compiles to CALL(dest ::NoneVar) + RETURN ::NoneVar,
                        // while a bare `return` compiles to RETURN null. Merge the ::NoneVar return
                        // into the immediately-preceding ::NoneVar-dest call statement to reproduce
                        // the form.
                        if (v.VariableType == VariableType.Identifier
                            && IdName(v).Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
                            && i > lo && IsNoneDestCall(_ins[i - 1])
                            && _pending.Count == 0 && stmts.Count > 0 && !stmts[^1].StartsWith("return"))
                        {
                            stmts[^1] = "return " + stmts[^1];
                            i++; break;
                        }
                        string stmt;
                        if (v.VariableType == VariableType.Null
                            || (v.VariableType == VariableType.Identifier && IdName(v).Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)))
                            stmt = "return";
                        else
                            stmt = $"return {Render(Resolve(v))}";
                        FlushPending(stmts);
                        stmts.Add(stmt);
                        i++; break;
                    }

                    case InstructionOpcode.ASSIGN:
                    {
                        var dest = IdName(a[0]);
                        var src = Resolve(a[1]);
                        // Materialized temps are real named locals now — writes are real assignments.
                        if (IsTemp(dest) && !Materialized.Contains(dest)) { SetPending(dest, src, stmts, Math.Min(_consumedStart, i)); i++; break; }
                        FlushPending(stmts);
                        stmts.Add($"{LhsName(dest)} = {Render(src)}");
                        i++; break;
                    }

                    case InstructionOpcode.PROPSET:
                    {
                        var prop = StrName(a[0]);
                        var obj = Resolve(a[1]);
                        var val = Resolve(a[2]);
                        // Self. prefix for the same reason as EProp rendering: a PROPSET on self in
                        // the bytecode must recompile to a PROPSET, not the bare-name backing-var write.
                        var lhs = IsSelf(obj) ? $"Self.{prop}" : $"{Postfix2(obj)}.{prop}";
                        FlushPending(stmts);
                        stmts.Add($"{lhs} = {Render(val)}");
                        i++; break;
                    }

                    case InstructionOpcode.ARRAY_SETELEMENT:
                    {
                        var arr = Resolve(a[0]);
                        var idx = Resolve(a[1]);
                        var val = Resolve(a[2]);
                        FlushPending(stmts);
                        stmts.Add($"{Postfix2(arr)}[{Render(idx)}] = {Render(val)}");
                        i++; break;
                    }

                    default:
                    {
                        // Value-producing instruction.
                        var (dest, expr) = Produce(ins);
                        if (dest == "")
                        {
                            // call with ::NoneVar dest -> bare call statement
                            FlushPending(stmts);
                            stmts.Add(Render(expr));
                            i++; break;
                        }
                        if (dest is null) throw new StructureException($"value op with no dest @{i}");
                        if (IsTemp(dest) && !Materialized.Contains(dest)) { SetPending(dest, expr, stmts, Math.Min(_consumedStart, i)); i++; break; }
                        FlushPending(stmts);
                        stmts.Add($"{LhsName(dest)} = {Render(expr)}");
                        i++; break;
                    }
                }
            }
            if (flushAtEnd) FlushPending(stmts, cont);   // discarded results / region-crossing values at block end
            return stmts;
        }

        bool ReturnsNone()
            => string.IsNullOrEmpty(_f.ReturnTypeName)
               || _f.ReturnTypeName.Equals("None", StringComparison.OrdinalIgnoreCase);

        /// <summary>Does this instruction read <paramref name="name"/> as a SOURCE operand?
        /// Source-arg positions per opcode shape (dest slots excluded).</summary>
        static bool ConsumesAsSource(PexObjectFunctionInstruction ins, string name)
        {
            var a = ins.Arguments;
            IEnumerable<int> srcIdx = ins.OpCode switch
            {
                InstructionOpcode.IADD or InstructionOpcode.FADD or InstructionOpcode.ISUB or InstructionOpcode.FSUB
                    or InstructionOpcode.IMUL or InstructionOpcode.FMUL or InstructionOpcode.IDIV or InstructionOpcode.FDIV
                    or InstructionOpcode.IMOD or InstructionOpcode.STRCAT
                    or InstructionOpcode.CMP_EQ or InstructionOpcode.CMP_LT or InstructionOpcode.CMP_LTE
                    or InstructionOpcode.CMP_GT or InstructionOpcode.CMP_GTE => new[] { 1, 2 },
                InstructionOpcode.NOT or InstructionOpcode.INEG or InstructionOpcode.FNEG
                    or InstructionOpcode.CAST or InstructionOpcode.ASSIGN => new[] { 1 },
                InstructionOpcode.JMPT or InstructionOpcode.JMPF or InstructionOpcode.RETURN => new[] { 0 },
                InstructionOpcode.CALLMETHOD => new[] { 1 }.Concat(Enumerable.Range(4, Math.Max(0, a.Count - 4))),
                InstructionOpcode.CALLPARENT => Enumerable.Range(3, Math.Max(0, a.Count - 3)),
                InstructionOpcode.CALLSTATIC => Enumerable.Range(4, Math.Max(0, a.Count - 4)),
                InstructionOpcode.PROPGET => new[] { 1 },
                InstructionOpcode.PROPSET => new[] { 1, 2 },
                InstructionOpcode.ARRAY_CREATE or InstructionOpcode.ARRAY_LENGTH => new[] { 1 },
                InstructionOpcode.ARRAY_GETELEMENT => new[] { 1, 2 },
                InstructionOpcode.ARRAY_SETELEMENT => new[] { 0, 1, 2 },
                InstructionOpcode.ARRAY_FINDELEMENT or InstructionOpcode.ARRAY_RFINDELEMENT => new[] { 0, 2, 3 },
                _ => Array.Empty<int>(),
            };
            return srcIdx.Any(ix => ix < a.Count
                && a[ix].VariableType == VariableType.Identifier
                && string.Equals(a[ix].StringValue, name, StringComparison.OrdinalIgnoreCase));
        }

        static bool BlockIsSingleIf(List<string> block)
        {
            // True when the whole block is one top-level if..endif (its endif is the last line).
            int depth = 0;
            for (int k = 0; k < block.Count; k++)
            {
                var s = block[k];
                if (!s.StartsWith("    "))
                {
                    if (s.StartsWith("if ")) depth++;
                    else if (s == "endif") { depth--; if (depth == 0 && k != block.Count - 1) return false; }
                    else if (depth == 1 && (s == "else" || s.StartsWith("elseif "))) { /* part of the same if */ }
                    else if (depth == 0) return false;
                }
            }
            return depth == 0;
        }

        /// <summary>Decode a value-producing instruction into (destName, expr). destName "" = ::NoneVar discard.</summary>
        (string? dest, Expr expr) Produce(PexObjectFunctionInstruction ins)
        {
            var a = ins.Arguments;
            switch (ins.OpCode)
            {
                case InstructionOpcode.IADD or InstructionOpcode.FADD:
                    return Bin(a, "+");
                case InstructionOpcode.ISUB or InstructionOpcode.FSUB:
                    return Bin(a, "-");
                case InstructionOpcode.IMUL or InstructionOpcode.FMUL:
                    return Bin(a, "*");
                case InstructionOpcode.IDIV or InstructionOpcode.FDIV:
                    return Bin(a, "/");
                case InstructionOpcode.IMOD:
                    return Bin(a, "%");
                case InstructionOpcode.STRCAT:
                    return Bin(a, "+");
                case InstructionOpcode.CMP_EQ: return Bin(a, "==");
                case InstructionOpcode.CMP_LT: return Bin(a, "<");
                case InstructionOpcode.CMP_LTE: return Bin(a, "<=");
                case InstructionOpcode.CMP_GT: return Bin(a, ">");
                case InstructionOpcode.CMP_GTE: return Bin(a, ">=");

                case InstructionOpcode.NOT:
                    return (IdName(a[0]), new EUn("!", Resolve(a[1])));
                case InstructionOpcode.INEG or InstructionOpcode.FNEG:
                    return (IdName(a[0]), new EUn("-", Resolve(a[1])));

                case InstructionOpcode.CAST:
                {
                    var dest = IdName(a[0]);
                    var src = Resolve(a[1]);
                    var destType = TypeOf(dest);
                    var srcType = a[1].VariableType switch
                    {
                        VariableType.Identifier => TypeOf(IdName(a[1])),
                        VariableType.Integer => "Int",
                        VariableType.Float => "Float",
                        VariableType.Bool => "Bool",
                        VariableType.String => "String",
                        _ => null,
                    };
                    // Bool and String casts are implicit in every Papyrus context — emit the bare
                    // operand and the compiler regenerates the same CAST. An explicit string cast
                    // would perturb STRCAT temp allocation.
                    // CAST of null = the compiler typing a None literal for a comparison — implicit too.
                    if (a[1].VariableType == VariableType.Null)
                        return (dest, src);
                    if (destType is not null &&
                        (destType.Equals("Bool", StringComparison.OrdinalIgnoreCase)
                         || destType.Equals("String", StringComparison.OrdinalIgnoreCase)
                         || (srcType is not null && srcType.Equals(destType, StringComparison.OrdinalIgnoreCase))))
                        return (dest, src);   // implicit / identity cast: pass through
                    // Int→Float and UPCASTS (dest is an ancestor class of src) are implicit in every
                    // context too — re-emitting them explicitly changes codegen (implicit arg
                    // conversions batch after all arg evals; explicit casts compile inline per-arg).
                    if (destType is not null && srcType is not null
                        && (destType.Equals("Float", StringComparison.OrdinalIgnoreCase)
                                && srcType.Equals("Int", StringComparison.OrdinalIgnoreCase)
                            || IsAncestorClass(destType, srcType)))
                        return (dest, src);
                    return (dest, new ECast(src, TypeName(destType ?? "?")));
                }

                case InstructionOpcode.CALLMETHOD:
                {
                    var name = StrName(a[0]);
                    var obj = Resolve(a[1]);
                    var dest = IdName(a[2]);
                    var args = CallArgs(a, 3);
                    var call = new ECall(IsSelf(obj) ? null : obj, name, args);
                    return (DestOrDiscard(dest), call);
                }
                case InstructionOpcode.CALLPARENT:
                {
                    var name = StrName(a[0]);
                    var dest = IdName(a[1]);
                    var args = CallArgs(a, 2);
                    return (DestOrDiscard(dest), new EParent(name, args));
                }
                case InstructionOpcode.CALLSTATIC:
                {
                    var cls = StrName(a[0]);
                    var name = StrName(a[1]);
                    var dest = IdName(a[2]);
                    var args = CallArgs(a, 3);
                    return (DestOrDiscard(dest), new EStatic(cls, name, args));
                }

                case InstructionOpcode.PROPGET:
                {
                    var prop = StrName(a[0]);
                    var obj = Resolve(a[1]);
                    var dest = IdName(a[2]);
                    return (dest, new EProp(IsSelf(obj) ? null : obj, prop));
                }

                case InstructionOpcode.ARRAY_CREATE:
                {
                    var dest = IdName(a[0]);
                    var t = TypeOf(dest) ?? throw new StructureException($"array_create dest {dest} has no type");
                    if (!t.EndsWith("[]")) throw new StructureException($"array_create dest {dest} type {t} not an array");
                    return (dest, new ENew(TypeName(t[..^2]), Resolve(a[1])));
                }
                case InstructionOpcode.ARRAY_LENGTH:
                    return (IdName(a[0]), new ELen(Resolve(a[1])));
                case InstructionOpcode.ARRAY_GETELEMENT:
                    return (IdName(a[0]), new EIndex(Resolve(a[1]), Resolve(a[2])));
                case InstructionOpcode.ARRAY_FINDELEMENT:
                    return (IdName(a[1]), new EFind(Resolve(a[0]), Resolve(a[2]), Resolve(a[3]), Reverse: false));
                case InstructionOpcode.ARRAY_RFINDELEMENT:
                    return (IdName(a[1]), new EFind(Resolve(a[0]), Resolve(a[2]), Resolve(a[3]), Reverse: true));

                default:
                    throw new StructureException($"unhandled opcode {ins.OpCode}");
            }

            (string?, Expr) Bin(IReadOnlyList<IPexObjectVariableDataGetter> args, string op)
            {
                var dest = IdName(args[0]);
                var l = Resolve(args[1]);
                var r = Resolve(args[2]);
                return (dest, new EBin(op, l, r));
            }
        }

        string? DestOrDiscard(string dest)
            => dest.Equals("::NoneVar", StringComparison.OrdinalIgnoreCase) ? "" : dest;

        List<Expr> CallArgs(IReadOnlyList<IPexObjectVariableDataGetter> a, int argcIdx)
        {
            if (a[argcIdx].VariableType != VariableType.Integer)
                throw new StructureException($"call argc not an int at arg {argcIdx}");
            int n = a[argcIdx].IntValue ?? 0;
            if (a.Count != argcIdx + 1 + n)
                throw new StructureException($"call argc {n} but {a.Count - argcIdx - 1} args present");
            var list = new List<Expr>(n);
            for (int k = 0; k < n; k++) list.Add(Resolve(a[argcIdx + 1 + k]));
            // Trailing RAW-null args are BAKED DEFAULTS: an explicitly-written None compiles to a
            // typed null-CAST temp passed by ident, never a raw null arg slot, so a raw null can only
            // mean the source omitted the arg. Re-omit it; emitting None would materialize an extra
            // cast temp the original never had.
            int keep = n;
            while (keep > 0 && a[argcIdx + keep].VariableType == VariableType.Null) keep--;
            if (keep < n) list.RemoveRange(keep, n - keep);
            return list;
        }

        Expr Resolve(IPexObjectVariableDataGetter d) => d.VariableType switch
        {
            VariableType.Null => new EConst("None"),
            VariableType.String => new EConst(Quote(d.StringValue ?? "")),
            VariableType.Integer => new EConst((d.IntValue ?? 0).ToString(CultureInfo.InvariantCulture)),
            VariableType.Float => new EConst(FloatText(d.FloatValue ?? 0f)),
            VariableType.Bool => new EConst(d.BoolValue == true ? "true" : "false"),
            VariableType.Identifier => ResolveIdent(IdName(d)),
            _ => throw new StructureException($"unknown VariableType {d.VariableType}"),
        };

        Expr ResolveIdent(string name)
        {
            if (_pending.TryGetValue(name, out var e))
            {
                _consumedStart = Math.Min(_consumedStart, _pendingStart[name]);
                DropPending(name);
                return e;
            }
            if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return new EIdent("Self");
            if (IsTemp(name) && !Materialized.Contains(name)) throw new StructureException($"temp {name} read with no pending value @{_cur}");
            return new EIdent(LhsName(name));
        }

        (Expr e, int start) Consume(string name, int at)
        {
            if (_pending.TryGetValue(name, out var e))
            {
                var start = _pendingStart[name];
                DropPending(name);
                return (e, start);
            }
            if (!IsTemp(name) || Materialized.Contains(name)) return (new EIdent(LhsName(name)), at);
            throw new StructureException($"condition temp {name} has no pending value @{at}");
        }

        static bool IsNoneDestCall(PexObjectFunctionInstruction ins)
        {
            int destIdx = ins.OpCode switch
            {
                InstructionOpcode.CALLMETHOD or InstructionOpcode.CALLSTATIC => 2,
                InstructionOpcode.CALLPARENT => 1,
                _ => -1,
            };
            return destIdx >= 0 && destIdx < ins.Arguments.Count
                && ins.Arguments[destIdx].VariableType == VariableType.Identifier
                && string.Equals(ins.Arguments[destIdx].StringValue, "::NoneVar", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Does any jump in [scanLo, scanHi) target an index in [rangeLo, rangeHi)?</summary>
        bool AnyJumpInto(int scanLo, int scanHi, int rangeLo, int rangeHi)
        {
            for (int k = scanLo; k < scanHi && k < _ins.Count; k++)
            {
                var kop = _ins[k].OpCode;
                if (kop is not (InstructionOpcode.JMP or InstructionOpcode.JMPF or InstructionOpcode.JMPT)) continue;
                int kt = k + IntArg(_ins[k].Arguments[kop == InstructionOpcode.JMP ? 0 : 1]);
                if (kt >= rangeLo && kt < rangeHi) return true;
            }
            return false;
        }

        /// <summary>Is <paramref name="name"/> read as a source operand in [lo, hi) before being
        /// written as a destination? Detects optimizer value-reuse of a condition temp.</summary>
        bool ReadsBeforeWrite(int lo, int hi, string name)
        {
            for (int k = lo; k < hi && k < _ins.Count; k++)
            {
                if (ConsumesAsSource(_ins[k], name)) return true;
                if (WritesDest(_ins[k], name)) return false;
            }
            return false;
        }

        /// <summary>Does this instruction write <paramref name="name"/> as its DESTINATION slot?
        /// (Inverse of ConsumesAsSource — dest positions per opcode shape.)</summary>
        static bool WritesDest(PexObjectFunctionInstruction ins, string name)
        {
            var a = ins.Arguments;
            int destIdx = ins.OpCode switch
            {
                InstructionOpcode.IADD or InstructionOpcode.FADD or InstructionOpcode.ISUB or InstructionOpcode.FSUB
                    or InstructionOpcode.IMUL or InstructionOpcode.FMUL or InstructionOpcode.IDIV or InstructionOpcode.FDIV
                    or InstructionOpcode.IMOD or InstructionOpcode.STRCAT
                    or InstructionOpcode.CMP_EQ or InstructionOpcode.CMP_LT or InstructionOpcode.CMP_LTE
                    or InstructionOpcode.CMP_GT or InstructionOpcode.CMP_GTE
                    or InstructionOpcode.NOT or InstructionOpcode.INEG or InstructionOpcode.FNEG
                    or InstructionOpcode.CAST or InstructionOpcode.ASSIGN
                    or InstructionOpcode.ARRAY_CREATE or InstructionOpcode.ARRAY_LENGTH
                    or InstructionOpcode.ARRAY_GETELEMENT => 0,
                InstructionOpcode.CALLPARENT or InstructionOpcode.ARRAY_FINDELEMENT
                    or InstructionOpcode.ARRAY_RFINDELEMENT => 1,
                InstructionOpcode.CALLMETHOD or InstructionOpcode.CALLSTATIC or InstructionOpcode.PROPGET => 2,
                _ => -1,
            };
            return destIdx >= 0 && destIdx < a.Count
                && a[destIdx].VariableType == VariableType.Identifier
                && string.Equals(a[destIdx].StringValue, name, StringComparison.OrdinalIgnoreCase);
        }

        string? TypeOf(string name)
            => _d._localTypes.TryGetValue(name, out var t) ? t
             : _d._objVarTypes.TryGetValue(name, out var v) ? v : null;

        /// <summary>Is <paramref name="ancestor"/> a (strict) ancestor class of <paramref name="type"/>
        /// per the injected class-parent map? False when no map is loaded — unknown hierarchies keep
        /// their explicit casts, which is conservative and still correct.</summary>
        bool IsAncestorClass(string ancestor, string type)
        {
            var map = _d._classParents;
            if (map is null) return false;
            var cur = type;
            for (int hops = 0; hops < 64; hops++)
            {
                if (!map.TryGetValue(cur, out var parent)) return false;
                if (parent.Equals(ancestor, StringComparison.OrdinalIgnoreCase)) return true;
                cur = parent;
            }
            return false;
        }

        static bool IsSelf(Expr e) => e is EIdent i && i.Name.Equals("Self", StringComparison.OrdinalIgnoreCase);
        static string Postfix2(Expr e) => e is EBin or EUn ? "(" + Render(e) + ")" : Render(e);
    }

    // ------------------------------------------------------------------ shared helpers
    /// <summary>Compiler expression temps only: <c>::temp&lt;N&gt;</c> and the ::NoneVar discard slot. Other
    /// ::-prefixed names (CK fragment ::mangled_* locals, ::X_var auto-prop backing) are real storage.</summary>
    static bool IsTemp(string name)
        => name.Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
           || (name.StartsWith("::temp", StringComparison.OrdinalIgnoreCase) && name.Length > 6 && char.IsDigit(name[6]));

    static int IntArg(IPexObjectVariableDataGetter d)
        => d.VariableType == VariableType.Integer
            ? d.IntValue ?? throw new StructureException("int arg with null value")
            : throw new StructureException($"expected int arg, got {d.VariableType}");

    /// <summary>Auto-property backing var ::X_var reads/writes surface as the property name X;
    /// other ::-prefixed locals (CK fragment mangles) get a legal sanitized identifier.</summary>
    static string LhsName(string name)
        => !name.StartsWith("::") ? name
            : name.EndsWith("_var", StringComparison.OrdinalIgnoreCase) ? name[2..^4]
            : name.TrimStart(':');

    static string IdName(IPexObjectVariableDataGetter d)
        => d.VariableType == VariableType.Identifier
            ? d.StringValue ?? throw new StructureException("identifier with null name")
            : throw new StructureException($"expected identifier, got {d.VariableType}");

    /// <summary>Call/prop names arrive as Identifier or String depending on slot — accept both.</summary>
    static string StrName(IPexObjectVariableDataGetter d)
        => d.VariableType is VariableType.Identifier or VariableType.String
            ? d.StringValue ?? throw new StructureException("name with null value")
            : throw new StructureException($"expected name, got {d.VariableType}");

    internal static string? InitText(IPexObjectVariableDataGetter? d) => d is null ? null : d.VariableType switch
    {
        VariableType.Null => null,
        VariableType.String => Quote(d.StringValue ?? ""),
        VariableType.Integer => (d.IntValue ?? 0).ToString(CultureInfo.InvariantCulture),
        VariableType.Float => FloatText(d.FloatValue ?? 0f),
        VariableType.Bool => d.BoolValue == true ? "true" : "false",
        _ => null,
    };

    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", '\r' => "", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    static string FloatText(float f)
    {
        var s = f.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E') || s.Contains('e'))
            s = f.ToString("F10", CultureInfo.InvariantCulture).TrimEnd('0');
        if (!s.Contains('.')) s += ".0";
        if (s.EndsWith(".")) s += "0";
        return s;
    }

    static string TypeName(string t) => t switch
    {
        _ when t.Equals("Int", StringComparison.OrdinalIgnoreCase) => "int",
        _ when t.Equals("Float", StringComparison.OrdinalIgnoreCase) => "float",
        _ when t.Equals("Bool", StringComparison.OrdinalIgnoreCase) => "bool",
        _ when t.Equals("String", StringComparison.OrdinalIgnoreCase) => "string",
        _ when t.Equals("Int[]", StringComparison.OrdinalIgnoreCase) => "int[]",
        _ when t.Equals("Float[]", StringComparison.OrdinalIgnoreCase) => "float[]",
        _ when t.Equals("Bool[]", StringComparison.OrdinalIgnoreCase) => "bool[]",
        _ when t.Equals("String[]", StringComparison.OrdinalIgnoreCase) => "string[]",
        _ => t,
    };
}

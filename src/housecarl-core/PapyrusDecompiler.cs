using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

/// <summary>PEX -> Papyrus source reconstruction over Mutagen's PexFile model, from codegen patterns each confirmed
/// against compiler output; a function whose flow matches no verified pattern FAILS LOUD and is counted. The patterns
/// and the contracts are in docs/architecture/papyrus.md.</summary>
public sealed class PapyrusDecompiler
{
    public sealed class Result
    {
        public string Source = "";
        public int FunctionsTotal;
        public int FunctionsFailed;
        public List<string> Failures = new();

        /// <summary>Count of flow patterns the canonical CK compiler never emits; what a non-zero count means is in
        /// docs/architecture/papyrus.md.</summary>
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
        // `as` binds tighter than a binary operator, so a binop operand keeps its own parens.
        ECast c => $"({(c.E is EBin ? "(" + Render(c.E) + ")" : Render(c.E))} as {c.Type})",
        ECall c => (c.Target is null ? "" : Postfix(c.Target) + ".") + c.Name + "(" + string.Join(", ", c.Args.Select(Render)) + ")",
        EStatic s => $"{s.Cls}.{s.Name}(" + string.Join(", ", s.Args.Select(Render)) + ")",
        EParent p => $"Parent.{p.Name}(" + string.Join(", ", p.Args.Select(Render)) + ")",
        // Self-property access keeps the explicit Self. prefix: a bare auto-property name compiles to the backing var.
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

    /// <summary>Optional child→parent class map, injected per call, enabling implicit-upcast suppression; null keeps
    /// every explicit cast. Contract in docs/architecture/papyrus.md.</summary>
    readonly IReadOnlyDictionary<string, string>? _classParents;

    /// <summary>Index of the first parameter a `= None` default is emitted on, by function name; the evidence a .pex
    /// carries for a default is in docs/architecture/papyrus.md.</summary>
    readonly Dictionary<string, int> _defaultedParams = new(StringComparer.OrdinalIgnoreCase);

    public PapyrusDecompiler(PexFile pex, PexObject obj, IReadOnlyDictionary<string, string>? classParents = null)
    {
        _pex = pex; _obj = obj; _classParents = classParents;
        // Object variables, backing vars included: TypeOf needs them for implicit-cast detection.
        foreach (var v in obj.Variables)
            if (v.Name is not null && v.TypeName is not null) _objVarTypes.TryAdd(v.Name, v.TypeName);
        HarvestBakedDefaults(obj);
    }

    /// <summary>Read every call this object makes to ITSELF and note the trailing run of raw-null arguments; the
    /// longest run any call shows is the one declared.</summary>
    void HarvestBakedDefaults(PexObject obj)
    {
        var own = new Dictionary<string, PexObjectFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in obj.States)
            foreach (var nf in st.Functions)
                if (nf.FunctionName is not null && nf.Function is not null) own.TryAdd(nf.FunctionName, nf.Function);

        // Every body this object carries, property Get and Set handlers included.
        var bodies = obj.States.SelectMany(st => st.Functions).Select(nf => nf.Function)
            .Concat(obj.Properties.SelectMany(pr => new[] { pr.ReadHandler, pr.WriteHandler }));

        foreach (var body in bodies)
            if (body is not null)
                foreach (var ins in body.Instructions)
                {
                    // CALLPARENT is left out: it runs the parent's function, whose own source declares it.
                    int nameIdx = ins.OpCode switch
                    {
                        InstructionOpcode.CALLMETHOD => 0,
                        InstructionOpcode.CALLSTATIC => 1,
                        _ => -1,
                    };
                    if (nameIdx < 0) continue;
                    var a = ins.Arguments;
                    const int argcIdx = 3;
                    if (a.Count <= argcIdx) continue;
                    // Whose function this call runs: another script's carries that script's defaults, not this one's.
                    bool toSelf = ins.OpCode == InstructionOpcode.CALLMETHOD
                        ? RunsThisObject(obj, body, a[1])
                        : string.Equals(a[0].StringValue, obj.Name, StringComparison.OrdinalIgnoreCase);
                    if (!toSelf) continue;
                    if (a[nameIdx].VariableType is not (VariableType.Identifier or VariableType.String)) continue;
                    var callee = a[nameIdx].StringValue;
                    if (callee is null || !own.TryGetValue(callee, out var target)) continue;
                    if (a[argcIdx].VariableType != VariableType.Integer) continue;
                    int n = a[argcIdx].IntValue ?? 0;
                    // The argument run lines up with the parameter list only when the counts match.
                    if (n != target.Parameters.Count || a.Count != argcIdx + 1 + n) continue;

                    int keep = n;
                    while (keep > 0
                           && a[argcIdx + keep].VariableType == VariableType.Null
                           && CanBeNone(target.Parameters[keep - 1].TypeName)) keep--;
                    if (keep == n) continue;
                    if (!_defaultedParams.TryGetValue(callee, out var seen) || keep < seen)
                        _defaultedParams[callee] = keep;
                }
    }

    /// <summary>Does a CALLMETHOD on this target run THIS object's function? `self` does, and so does anything
    /// declared as this object's own class.</summary>
    bool RunsThisObject(PexObject obj, PexObjectFunction body, IPexObjectVariableDataGetter target)
    {
        if (target.VariableType != VariableType.Identifier || target.StringValue is null) return false;
        var name = target.StringValue;
        if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return true;
        var type = body.Parameters.FirstOrDefault(v => name.Equals(v.Name, StringComparison.OrdinalIgnoreCase))?.TypeName
                   ?? body.Locals.FirstOrDefault(v => name.Equals(v.Name, StringComparison.OrdinalIgnoreCase))?.TypeName
                   ?? (_objVarTypes.TryGetValue(name, out var t) ? t : null);
        return type is not null && string.Equals(type, obj.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is `None` a legal value for this declared type? The four scalars are the ones it is not.</summary>
    static bool CanBeNone(string? type)
        => type is not null
           && !(type.Equals("int", StringComparison.OrdinalIgnoreCase)
                || type.Equals("float", StringComparison.OrdinalIgnoreCase)
                || type.Equals("bool", StringComparison.OrdinalIgnoreCase)
                || type.Equals("string", StringComparison.OrdinalIgnoreCase));

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
            // Conditional on an auto property lands on the BACKING VARIABLE's user flags: merge both.
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

        // A property handler is emitted as Get or Set, so a same-named function's default is not this one's.
        int firstDefaulted = !propertyHandler && _defaultedParams.TryGetValue(name, out var dp) ? dp : int.MaxValue;
        var ps = string.Join(", ", f.Parameters.Select((p, k) =>
            $"{TypeName(p.TypeName)} {p.Name}" + (k >= firstDefaulted ? " = None" : "")));
        var kw = asEvent ? "Event" : "Function";
        var header = $"{ind}{(ret is not null ? ret + " " : "")}{kw} {name}({ps})"
                   + (isGlobal ? " Global" : "") + (isNative ? " Native" : "");

        _localTypes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in f.Parameters) _localTypes[p.Name] = p.TypeName;
        foreach (var l in f.Locals) _localTypes[l.Name] = l.TypeName;

        _sb.AppendLine(header);
        Doc(f.DocString, ind + "    ");
        if (isNative) return;   // native: declaration only

        // Structure FIRST: materialized temps are only known after the walk, and they are declared as locals.
        List<string>? stmts = null;
        StructureException? fail = null;
        var body = new Body(this, f);
        try { stmts = body.Structure(0, f.Instructions.Count); }
        catch (StructureException ex) { fail = ex; }

        // Locals: declared at the first assignment when that is provably scope-safe, hoisted otherwise, because the
        // locals-table order the compiler allocates from follows where it sees the declaration.
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

    /// <summary>Rewrite each local's first-assignment line to a declaration-with-initializer when every other
    /// reference stays inside that assignment's block; returns the table names placed inline.</summary>
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
            // Block containment: the write's block runs until the first line that dedents below it.
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
        // Highest instruction index at which a pending value runs a call, int.MinValue when it runs none.
        readonly Dictionary<string, int> _pendingLastCall = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _pendingOrder = new();
        int _consumedStart;      // min start-index of pending values consumed while decoding the current instruction
        int _consumedLastCall;   // max last-call index of those same values
        int _cur;                // index of the instruction currently being decoded (diagnostics)

        /// <summary>Temps promoted to named locals, because a re-read temp cannot be single-use; declared at function
        /// top, and read and written by name.</summary>
        public readonly HashSet<string> Materialized = new(StringComparer.OrdinalIgnoreCase);

        void SetPending(string name, Expr e, List<string> stmts, int startIdx, int lastCallIdx)
        {
            // Overwriting an unconsumed pending discarded the earlier value: it is emitted here, at its own
            // position, so anything produced before it comes out first.
            if (_pending.TryGetValue(name, out var old))
            {
                FlushPending(stmts, _cur + 1, _pendingStart[name]);
                if (EmitsAnInstruction(old)) stmts.Add(Render(old));
                else throw new StructureException($"pending non-statement value on {name} overwritten ({old.GetType().Name})");
                DropPending(name);
            }
            _pending[name] = e;
            _pendingStart[name] = startIdx;
            _pendingLastCall[name] = lastCallIdx;
            _pendingOrder.Add(name);
        }

        void DropPending(string name)
        {
            _pending.Remove(name);
            _pendingStart.Remove(name);
            _pendingLastCall.Remove(name);
            _pendingOrder.Remove(name);
        }

        /// <summary>Unconsumed values pending at a statement boundary: a temp read downstream is materialized as a
        /// named local, and the rest are discarded results emitted in evaluation order as bare statements (the
        /// contract and its two exceptions are in docs/architecture/papyrus.md). `startBound` is the cut a statement
        /// carrying a pending value drains up to, int.MaxValue draining everything as at a region end.</summary>
        void FlushPending(List<string> stmts) => FlushPending(stmts, _cur + 1, _consumedStart);

        void FlushPending(List<string> stmts, int scanFrom) => FlushPending(stmts, scanFrom, int.MaxValue);

        void FlushPending(List<string> stmts, int scanFrom, int startBound)
        {
            // Production order, not creation order: a pure fold starts where the call it folds in does.
            foreach (var name in _pendingOrder.OrderBy(n => _pendingStart[n]).ToList())
            {
                if (_pendingStart[name] >= startBound) continue;
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
                if (!EmitsAnInstruction(e))
                    throw new StructureException($"leftover non-statement pending temp {name} ({e.GetType().Name})");
                stmts.Add(Render(e));
                DropPending(name);
            }
        }

        /// <summary>Flush the pending values that are discarded statements, in production order, leaving everything
        /// else in place; used at a short-circuit boundary, where a value the arm or the join still reads has to
        /// survive into the combined expression.</summary>
        void FlushPendingStatements(List<string> stmts, int scanFrom)
        {
            foreach (var name in _pendingOrder.OrderBy(n => _pendingStart[n]).ToList())
            {
                // A temp read downstream is a value, not a statement — the later flush materializes it.
                if (IsTemp(name) && ReadsBeforeWrite(scanFrom, _ins.Count, name)) continue;
                if (!EmitsAnInstruction(_pending[name])) continue;
                stmts.Add(Render(_pending[name]));
                DropPending(name);
            }
        }

        /// <summary>Is this instruction's own effect a call?</summary>
        static bool IsCallOpcode(InstructionOpcode op) =>
            op is InstructionOpcode.CALLMETHOD or InstructionOpcode.CALLSTATIC or InstructionOpcode.CALLPARENT;

        /// <summary>Is this name a function local or parameter? A store to one is invisible to a held-back call; a
        /// store to a script member, backing var included, is not.</summary>
        bool IsFunctionScoped(string name)
            => _f.Locals.Any(l => name.Equals(l.Name, StringComparison.OrdinalIgnoreCase))
               || _f.Parameters.Any(p => name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Refuse a held-back value that would have to cross this statement's effect, an order the source
        /// cannot express (#792); <paramref name="effectIndex"/> is where the statement's own effect happens.</summary>
        void RefuseCrossing(string what, int effectIndex)
        {
            foreach (var name in _pendingOrder)
                if (_pendingStart[name] >= _consumedStart && _pendingStart[name] < effectIndex
                    && CanObserveAnEffect(name))
                    throw new StructureException(
                        $"{what} @{_cur} runs after pending {name} in the stream and would be emitted before it, which is an order the source cannot express");
        }

        /// <summary>Can this pending value tell whether it ran before or after a statement's effect? Only if it runs a
        /// call or reads through an object.</summary>
        bool CanObserveAnEffect(string name)
            => _pendingLastCall[name] != int.MinValue || ReadsThroughAnObject(_pending[name]);

        bool ReadsThroughAnObject(Expr e) => e switch
        {
            EProp or EIndex or ELen or EFind or ECall or EStatic or EParent => true,
            // A bare identifier is a member read unless it names something in function scope.
            EIdent id => !IsFunctionScopedRendered(id.Name),
            EBin b => ReadsThroughAnObject(b.L) || ReadsThroughAnObject(b.R),
            EUn u => ReadsThroughAnObject(u.E),
            ECast c => ReadsThroughAnObject(c.E),
            ENew n => ReadsThroughAnObject(n.Size),
            _ => false,
        };

        /// <summary>Is this RENDERED name a function local or parameter? Both sides go through
        /// <see cref="LhsName"/>, and `Self` counts as scoped.</summary>
        bool IsFunctionScopedRendered(string rendered)
            => rendered.Equals("Self", StringComparison.OrdinalIgnoreCase)
               || _f.Locals.Any(l => rendered.Equals(LhsName(l.Name), StringComparison.OrdinalIgnoreCase))
               || _f.Parameters.Any(pm => rendered.Equals(LhsName(pm.Name), StringComparison.OrdinalIgnoreCase));

        /// <summary>The same refusal from the other side, for a statement that drains everything pending before it: a
        /// value produced after the one it carries cannot be drained ahead of it (#792).</summary>
        void RefuseDrainingPast(string what, int carriedStart)
        {
            foreach (var name in _pendingOrder)
                if (_pendingStart[name] > carriedStart && CanObserveAnEffect(name))
                    throw new StructureException(
                        $"{what} @{_cur} carries a value produced before pending {name}, which cannot be ordered either side of it");
        }
        /// <summary>Does writing this expression as a bare statement compile back to the instruction it came from?
        /// The two kinds that do not are in docs/architecture/papyrus.md.</summary>
        static bool EmitsAnInstruction(Expr e) => e is not EIdent && ReadsOrCalls(e);

        /// <summary>Does anything here have to be read or called at runtime, rather than folding to a literal?</summary>
        static bool ReadsOrCalls(Expr e) => e switch
        {
            EConst => false,
            EBin b => ReadsOrCalls(b.L) || ReadsOrCalls(b.R),
            EUn u => ReadsOrCalls(u.E),
            ECast c => ReadsOrCalls(c.E),
            _ => true,
        };

        public Body(PapyrusDecompiler d, PexObjectFunction f)
        {
            _d = d; _f = f;
            _ins = f.Instructions.Cast<PexObjectFunctionInstruction>().ToList();
        }

        // Function-level region: jumping one past the last instruction ends the function, so it is an exit.
        public List<string> Structure(int lo, int hi) => Structure(lo, hi, flushAtEnd: true, exits: new HashSet<int> { hi }, cont: hi);

        /// <summary>Structure one region. flushAtEnd=false for a short-circuit arm, whose pending values must survive
        /// into the enclosing condition. <paramref name="exits"/> holds the indices provably equivalent to falling off
        /// this region's end, which jump threading can make an enclosing join; a jump beyond hi that is not one stays a
        /// loud failure. <paramref name="cont"/> is where control resumes after the region, which the region-end flush
        /// scans from.</summary>
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
                _consumedLastCall = int.MinValue;
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
                        // A trailing JMP to an exit-equivalent index is a no-op: the region ends there anyway.
                        if (exits.Contains(t) && i == hi - 1) { _d._res.OptimizerHints++; i++; break; }
                        // Dead jump: a JMP immediately after a return is unreachable, so skipping it changes nothing.
                        if (stmts.Count > 0 && (stmts[^1] == "return" || stmts[^1].StartsWith("return ")))
                        { i++; break; }
                        // A reachable jump past the last instruction of a None-returning function IS a return; a
                        // value-returning one stays loud, its fall-off semantics being unverified.
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
                            // A jump-threaded false path: clamp an exit-equivalent target to the region end, else fail loud.
                            if (!exits.Contains(target))
                                throw new StructureException($"conditional jump @{i} -> {target} escapes region end {hi}");
                            target = hi;
                        }

                        // Short-circuit: the arm leaves the right operand in the SAME temp and the join still reads it.
                        // The jump usually lands ON that consumer, and further on when later call arguments evaluate
                        // in between. An arm that READS the temp first is a guarded block the promotion path below
                        // owns; an arm is straight-line and its condition is always a temp the promotion path has not
                        // taken. Pinned by DecompileShortCircuitTests.
                        if (condName is not null && IsTemp(condName) && !Materialized.Contains(condName) && target < hi
                            && (ConsumesAsSource(_ins[target], condName)
                                || (WritesDestIn(i + 1, target, condName)
                                    && (target - 1 <= i || _ins[target - 1].OpCode != InstructionOpcode.JMP)
                                    && !ReadsBeforeWrite(i + 1, target, condName)
                                    && ReadsBeforeWrite(target, hi, condName))))
                        {
                            var (left, leftStart, leftLastCall) = Consume(condName, i);
                            // Statements that precede the if drain now, in order; one produced after the left operand
                            // is not one of them.
                            RefuseDrainingPast("condition", leftStart);
                            FlushPendingStatements(stmts, i + 1);
                            // Evaluate the right side (cur+1 .. target) — must produce only pending values.
                            var sub = Structure(i + 1, target, flushAtEnd: false, exits: new HashSet<int>(), cont: target);
                            // The arm is lazily evaluated, so a statement in it fails loud and is named.
                            if (sub.Count > 0)
                                throw new StructureException(
                                    $"short-circuit arm @{i + 1}..{target} evaluates a statement, not just a value (first: {sub[0].Trim()})");
                            var (right, _, rightLastCall) = Consume(condName, target);
                            var combined = new EBin(op == InstructionOpcode.JMPF ? "&&" : "||", left, right);
                            SetPending(condName, combined, stmts, leftStart, Math.Max(leftLastCall, rightLastCall));
                            i = target;
                            continue;
                        }

                        Expr cond; int condStart;
                        if (condName is not null) (cond, condStart, _) = Consume(condName, i);
                        else { cond = Resolve(a[0]); condStart = i; }

                        bool isWhile = target - 1 > i && target - 1 < hi
                            && _ins[target - 1].OpCode == InstructionOpcode.JMP
                            && (target - 1) + IntArg(_ins[target - 1].Arguments[0]) <= i;

                        // Drain every other pending BEFORE the condition temp materializes: a value left pending
                        // across the branch would be consumed inside an arm, conditionally rather than once.
                        RefuseDrainingPast("condition", condStart);
                        FlushPending(stmts, _cur + 1);

                        // A condition temp read again inside the guarded block is promoted to a named local — never
                        // for a while, which re-evaluates its condition, and never for the ::NoneVar discard slot,
                        // which is no declarable local; a re-read in either stays a loud failure.
                        if (!isWhile && condName is not null && IsTemp(condName)
                            && !condName.Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
                            && ReadsBeforeWrite(i + 1, target, condName))
                        {
                            Materialized.Add(condName);
                            _d._res.OptimizerHints++;   // value-reused condition temp
                            stmts.Add($"{LhsName(condName)} = {Render(cond)}");
                            cond = new EIdent(LhsName(condName));
                        }

                        // Statement-level JMPT is an inverted branch, and one the CK compiler never emits, so
                        // reaching here counts as an optimizer hint.
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
                            // An m beyond hi is legal when exit-equivalent; a jump INTO the would-be else range means
                            // the range is fall-through code, so the if is read as no-else.
                            if (m >= target && (m <= hi || exits.Contains(m))
                                && !AnyJumpInto(i + 1, target - 1, target, m))
                            { thenHi = target - 1; elseHi = Math.Min(m, hi); }
                        }
                        // Child regions inherit their own join slots, plus the parent's exits when the joins coincide.
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
                        // `return <NoneCall>()` is a call into ::NoneVar then a return of it, while a bare `return`
                        // returns null — so the pending value is taken only when nothing else is pending.
                        if (v.VariableType == VariableType.Identifier
                            && IdName(v).Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
                            && _pending.Count == 1 && _pending.ContainsKey("::NoneVar"))
                        {
                            var (call, _, _) = Consume("::NoneVar", i);
                            FlushPending(stmts, _cur + 1);
                            stmts.Add("return " + Render(call));
                            i++; break;
                        }
                        string stmt;
                        if (v.VariableType == VariableType.Null
                            || (v.VariableType == VariableType.Identifier && IdName(v).Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)))
                            stmt = "return";
                        else
                            stmt = $"return {Render(Resolve(v))}";
                        // A return ENDS the region, so it drains wide — and refuses when the value it carries was
                        // produced first, which no ordering can express.
                        RefuseDrainingPast("return", _consumedStart);
                        FlushPending(stmts, _cur + 1);
                        stmts.Add(stmt);
                        i++; break;
                    }

                    case InstructionOpcode.ASSIGN:
                    {
                        var dest = IdName(a[0]);
                        var src = Resolve(a[1]);
                        // Materialized temps are real named locals now — writes are real assignments.
                        if (IsTemp(dest) && !Materialized.Contains(dest)) { SetPending(dest, src, stmts, Math.Min(_consumedStart, i), _consumedLastCall); i++; break; }
                        // A store to a local or parameter is invisible to a held-back call; a store to a member is not.
                        RefuseCrossing("store", IsFunctionScoped(dest) ? _consumedLastCall : i);
                        FlushPending(stmts);
                        stmts.Add($"{LhsName(dest)} = {Render(src)}");
                        i++; break;
                    }

                    case InstructionOpcode.PROPSET:
                    {
                        var prop = StrName(a[0]);
                        var obj = Resolve(a[1]);
                        var val = Resolve(a[2]);
                        // Self. prefix for the same reason as EProp rendering: a PROPSET must recompile to a PROPSET.
                        var lhs = IsSelf(obj) ? $"Self.{prop}" : $"{Postfix2(obj)}.{prop}";
                        // A property set can be a real setter function, so a held-back call cannot cross it.
                        RefuseCrossing("property set", i);
                        FlushPending(stmts);
                        stmts.Add($"{lhs} = {Render(val)}");
                        i++; break;
                    }

                    case InstructionOpcode.ARRAY_SETELEMENT:
                    {
                        var arr = Resolve(a[0]);
                        var idx = Resolve(a[1]);
                        var val = Resolve(a[2]);
                        // An array is a reference, so a held-back call cannot cross this store.
                        RefuseCrossing("array element set", i);
                        FlushPending(stmts);
                        stmts.Add($"{Postfix2(arr)}[{Render(idx)}] = {Render(val)}");
                        i++; break;
                    }

                    default:
                    {
                        // A value-producing instruction: its effect is its own call, else the last call folded in.
                        var (dest, expr) = Produce(ins);
                        // A property get and an array element read are effects where they run, like a call; an array's
                        // LENGTH is not, being unable to change under one.
                        bool runsHere = IsCallOpcode(op)
                            || op is InstructionOpcode.PROPGET or InstructionOpcode.ARRAY_GETELEMENT
                                  or InstructionOpcode.ARRAY_FINDELEMENT or InstructionOpcode.ARRAY_RFINDELEMENT;
                        int effect = IsCallOpcode(op) ? i : _consumedLastCall;
                        if (dest == "")
                        {
                            // A call into the ::NoneVar discard slot: a bare-call statement, unless the very next
                            // instruction reads that slot, which is how `x = obj.VoidCall()` compiles. The call runs
                            // here, so a value produced after the one it carries cannot be ordered either side of it.
                            RefuseCrossing("call", effect);
                            FlushPending(stmts);
                            if (NextReadsNoneVar(i + 1, hi))
                                SetPending("::NoneVar", expr, stmts, Math.Min(_consumedStart, i), effect);
                            else
                                stmts.Add(Render(expr));
                            i++; break;
                        }
                        if (dest is null) throw new StructureException($"value op with no dest @{i}");
                        if (IsTemp(dest) && !Materialized.Contains(dest))
                        {
                            // It runs here even though its result only goes pending, so the crossing settles at the fold.
                            if (runsHere) RefuseCrossing(IsCallOpcode(op) ? "call" : "read", i);
                            SetPending(dest, expr, stmts, Math.Min(_consumedStart, i), effect);
                            i++; break;
                        }
                        // The write is the same store as a plain assignment.
                        RefuseCrossing(IsCallOpcode(op) ? "call" : "store", IsFunctionScoped(dest) ? effect : i);
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

        /// <summary>Does this instruction read <paramref name="name"/> as a SOURCE operand?</summary>
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
                    // A CAST of null is the compiler typing a None literal, which is implicit like the rest.
                    if (a[1].VariableType == VariableType.Null)
                        return (dest, src);
                    // A None-typed source is the discard slot: `<none expression> as X` is not writable.
                    if (srcType is not null && srcType.Equals("None", StringComparison.OrdinalIgnoreCase))
                        return (dest, src);
                    if (destType is not null &&
                        (destType.Equals("Bool", StringComparison.OrdinalIgnoreCase)
                         || destType.Equals("String", StringComparison.OrdinalIgnoreCase)
                         || (srcType is not null && srcType.Equals(destType, StringComparison.OrdinalIgnoreCase))))
                        return (dest, src);   // implicit / identity cast: pass through
                    // Int→Float and an upcast are implicit too; re-emitting either explicitly changes codegen.
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
            // Trailing RAW-null args are baked defaults the source omitted, so they are re-omitted (#786).
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
                _consumedLastCall = Math.Max(_consumedLastCall, _pendingLastCall[name]);
                DropPending(name);
                return e;
            }
            if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return new EIdent("Self");
            if (IsTemp(name) && !Materialized.Contains(name)) throw new StructureException($"temp {name} read with no pending value @{_cur}");
            return new EIdent(LhsName(name));
        }

        (Expr e, int start, int lastCall) Consume(string name, int at)
        {
            if (_pending.TryGetValue(name, out var e))
            {
                var start = _pendingStart[name];
                var lastCall = _pendingLastCall[name];
                DropPending(name);
                return (e, start, lastCall);
            }
            if (!IsTemp(name) || Materialized.Contains(name)) return (new EIdent(LhsName(name)), at, int.MinValue);
            throw new StructureException($"condition temp {name} has no pending value @{at}");
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

        /// <summary>Is <paramref name="name"/> read as a source in [lo, hi) before being written?</summary>
        bool ReadsBeforeWrite(int lo, int hi, string name)
        {
            for (int k = lo; k < hi && k < _ins.Count; k++)
            {
                if (ConsumesAsSource(_ins[k], name)) return true;
                if (WritesDest(_ins[k], name)) return false;
            }
            return false;
        }

        /// <summary>Does the first real instruction at or after <paramref name="lo"/>, NOPs skipped, read ::NoneVar as
        /// a source? Only that adjacent read can take a pending None call.</summary>
        bool NextReadsNoneVar(int lo, int hi)
        {
            for (int k = lo; k < hi && k < _ins.Count; k++)
            {
                if (_ins[k].OpCode == InstructionOpcode.NOP) continue;
                return ConsumesAsSource(_ins[k], "::NoneVar");
            }
            return false;
        }

        /// <summary>Is <paramref name="name"/> written as a destination anywhere in [lo, hi)? A guarded block that
        /// never touches the condition temp is not a short-circuit arm; pinned by
        /// <c>DecompileShortCircuitTests.AGuardedBlockThatNeverTouchesItsConditionTempIsNotReportedAsAnArm</c>.</summary>
        bool WritesDestIn(int lo, int hi, string name)
        {
            for (int k = lo; k < hi && k < _ins.Count; k++)
                if (WritesDest(_ins[k], name)) return true;
            return false;
        }

        /// <summary>Does this instruction write <paramref name="name"/> as its DESTINATION slot?</summary>
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

        /// <summary>Is <paramref name="ancestor"/> a strict ancestor class of <paramref name="type"/> per the injected
        /// map? False when no map is loaded.</summary>
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
    /// <summary>Compiler expression temps only; every other ::-prefixed name is real storage.</summary>
    static bool IsTemp(string name)
        => name.Equals("::NoneVar", StringComparison.OrdinalIgnoreCase)
           || (name.StartsWith("::temp", StringComparison.OrdinalIgnoreCase) && name.Length > 6 && char.IsDigit(name[6]));

    static int IntArg(IPexObjectVariableDataGetter d)
        => d.VariableType == VariableType.Integer
            ? d.IntValue ?? throw new StructureException("int arg with null value")
            : throw new StructureException($"expected int arg, got {d.VariableType}");

    /// <summary>An auto-property backing var surfaces as the property name; another ::-prefixed local is sanitized.</summary>
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

using Mutagen.Bethesda.Plugins;
using Named = HousecarlCore.DistributorLines.Named;
using Resolved = HousecarlCore.DistributorLines.Resolved;

namespace HousecarlCore;

/// <summary>BOS's <c>_SWAP.ini</c> lines read as BOS 3.4.0 and 3.5.0 read them (rules and sources:
/// docs/architecture/bos-layer.md): every section, merged in any case and kept in first appearance, each key once; the
/// section's name picks the map; a swap line is base|swap|properties|chance and a property line base|properties|chance;
/// a conditional header's conditions go with every line under it. Every form a line names resolves through the one
/// target resolver under BOS's row and is labelled target (the base), value (a swap) or filter (a condition), with what
/// BOS does where a name finds nothing, by field. Conditions, chance and properties are not evaluated against references.</summary>
public static class BosLines
{
    /// <summary>One condition of a section's header: whether a leading '-' negates it, and the term after it.</summary>
    public sealed record Condition(bool Not, string Raw);

    /// <summary>One section as SimpleIni keeps it: its name as first spelt, its place among the file's sections, the
    /// map its lines go to, its conditions, and the key BOS's conditional maps file them under.</summary>
    public sealed record Section(string Name, int Order, BosKind Kind, IReadOnlyList<Condition> Conditions, string ConditionId);

    /// <summary>One line of a file as BOS splits it: the key BOS reads (the text before any '='), its '|' fields, its
    /// section, why BOS reads no entry from it (null when it reads one) and the notes the split owes.</summary>
    public sealed record Line(int Number, string Raw, string Key, IReadOnlyList<string> Fields, Section Section,
                              string? Skipped, IReadOnlyList<string> Notes) : DistributorLines.ILine
    {
        /// <summary>The header's count key: the map the line goes to.</summary>
        public string HeaderKey => BosGrammar.Name(Section.Kind);
    }

    // ---- splitting a file ---------------------------------------------------------------

    /// <summary>Every key line of one file's text in file order, read or skipped, as SimpleIni and BOS meet it, under
    /// <paramref name="format"/>'s line grammar. <paramref name="file"/> is the file's name, which a conditional
    /// section's key begins with.</summary>
    public static IReadOnlyList<Line> Parse(string text, DistributorFormat format, string file)
    {
        var g = format.BosLines!;
        var lines = new List<Line>();
        var sections = new Dictionary<string, (Section S, Dictionary<string, int> Keys)>(StringComparer.OrdinalIgnoreCase);
        (Section S, Dictionary<string, int> Keys) Open(string name)
        {
            if (sections.TryGetValue(name, out var s)) return s;
            return sections[name] = (MakeSection(name, sections.Count, g, file), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }
        // SimpleIni files a section when its header or, for the unnamed one, its first key is read.
        string current = "";
        var physical = text.Split('\n');
        for (int i = 0; i < physical.Length; i++)
        {
            var raw = physical[i].TrimEnd('\r');
            var t = raw.Trim();
            if (t.Length == 0 || g.CommentLeads.Contains(t[0])) continue;
            if (t[0] == '[')
            {
                int close = t.IndexOf(']');
                if (close < 0) { lines.Add(Skip(i + 1, raw, "opens a section with '[' and has no ']', so SimpleIni reads no section and no key from it")); continue; }
                current = t[1..close].Trim();
                Open(current);
                continue;
            }
            int eq = t.IndexOf(g.Assign);
            if (eq == 0) { lines.Add(Skip(i + 1, raw, $"has no key before '{g.Assign}'")); continue; }
            var key = eq > 0 ? t[..eq].TrimEnd() : t;
            var notes = new List<string>();
            if (eq > 0) notes.Add($"holds '{g.Assign}', and BOS reads only the key before it, '{key}'");
            var sec = Open(current);
            if (sec.Keys.TryGetValue(key, out var first))
            {
                lines.Add(Skip(i + 1, raw, $"repeats line :{first} of its section (in any case), and SimpleIni keeps each key of a section once, so BOS reads it once, at :{first}"));
                continue;
            }
            sec.Keys[key] = i + 1;
            var fields = key.Split(g.Fields);
            bool swaps = BosGrammar.Swaps(sec.S.Kind);
            if (fields.Length < 2)
            {
                lines.Add(Skip(i + 1, raw, $"has no '{g.Fields}', so it has no {(swaps ? "swap" : "properties")} field, and BOS reads that field past the end of the line's fields, which is undefined, so what BOS does with it is not claimed"));
                continue;
            }
            int most = swaps ? 4 : 3;
            if (fields.Length > most) notes.Add($"has {fields.Length} '{g.Fields}' fields; BOS reads the first {most} and ignores the rest");
            lines.Add(new Line(i + 1, raw, key, fields, sec.S, null, notes));
        }
        return lines;
    }

    // A skipped line is filed under no section: no map reads it.
    static readonly Section None = new("", int.MaxValue, BosKind.References, Array.Empty<Condition>(), "");

    static Line Skip(int n, string raw, string why) => new(n, raw, "", Array.Empty<string>(), None, why, Array.Empty<string>());

    static Section MakeSection(string name, int order, BosGrammar g, string file)
    {
        var kind = g.KindOf(name);
        if (kind is not (BosKind.ConditionalForms or BosKind.ConditionalProperties)) return new Section(name, order, kind, Array.Empty<Condition>(), "");
        var parts = name.Split(g.Conditional);
        var conditions = parts[1].Split(g.Item).Select(c => c.Length > 0 && c[0] == g.Not ? new Condition(true, c[1..]) : new Condition(false, c)).ToList();
        return new Section(name, order, kind, conditions, file + g.Conditional + parts[1]);
    }

    // ---- what a line names --------------------------------------------------------------

    /// <summary>What one line names and what BOS does with it: every form it names that resolves, the notes its tokens
    /// owe, why BOS drops the line (null when it keeps it), and the runtime FormIDs of the bases it files the line under.</summary>
    public sealed record LineRead(IReadOnlyList<Named> Forms, IReadOnlyList<string> Notes, string? Dropped,
                                  IReadOnlyList<(uint Id, string Token)> Bases) : DistributorLines.IRead
    {
        /// <summary>BOS names no plugin as a term.</summary>
        public IReadOnlyList<string> PluginTerms => Array.Empty<string>();
    }

    /// <summary>The lookups a read needs: the call's target resolver, the class of a resolved record off the order, and the format's row.</summary>
    public sealed record Context(SkyPatcherOverlay.IFormResolver Resolver, Func<FormKey, Type?> ClassOf, DistributorFormat Format)
    {
        internal BosGrammar G => Format.BosLines!;
        internal TargetSpelling S => Format.Targets;
    }

    /// <summary>One token as BOS takes it: the resolve, the FormID BOS files it by (null when BOS gets 0, its "not
    /// found"), and whether BOS's FormID read throws on it.</summary>
    sealed record Taken(Resolved R, uint? Id, bool Throws);

    /// <summary>Read every line of <paramref name="lines"/> as BOS does: each distinct token resolved once (an every-type
    /// lookup, as BOS's untyped one is). A skipped line reads as null.</summary>
    public static IReadOnlyList<LineRead?> ReadAll(IReadOnlyList<Line> lines, Context ctx)
    {
        // A token's read depends only on its text in this call, so each distinct one is resolved once; the map dies with the call.
        var tokens = new Dictionary<string, Taken>(StringComparer.Ordinal);
        Taken Of(string raw) => tokens.TryGetValue(raw, out var t) ? t : tokens[raw] = Take(raw, ctx);
        return lines.Select(l => l.Skipped is not null ? null : ReadLine(l, Of, ctx)).ToList();
    }

    static Taken Take(string raw, Context ctx)
    {
        var read = RuntimeTargets.Read(raw, ctx.S);
        var r = DistributorLines.Resolve(raw, ctx.Resolver, ctx.S, "BOS");
        uint? id = read.Kind switch
        {
            RuntimeTargets.TargetKind.Address when read.Address!.FormId is not null && ctx.Resolver.PluginPresent(read.Address.Plugin!)
                => RuntimeTargets.RuntimeAdd(read.Address, ctx.Resolver),
            RuntimeTargets.TargetKind.RuntimeFormId => read.FormId,
            _ => r.Form is { } fk ? RuntimeTargets.RuntimeAdd(new FormAddress(fk.ModKey.FileName, fk.ID, null), ctx.Resolver) : null,
        };
        return new Taken(r, id is 0 ? null : id, read.Kind == RuntimeTargets.TargetKind.Address && read.Address!.FormId is null);
    }

    const string StoulThrows = "BOS's FormID read (std::stoul) throws on it and BOS catches nothing, so its load stops there; what the game does then is not read";

    static LineRead ReadLine(Line line, Func<string, Taken> of, Context ctx)
    {
        var g = ctx.G;
        var forms = new List<Named>();
        var notes = new List<string>();
        var bases = new List<(uint, string)>();
        string? dropped = null;
        void Drop(string why) => dropped ??= why;
        string Log(string why) => $"BOS logs 'fail : [{line.Key}] ({why})' and drops the line";

        // The section's conditions, read for every line under it.
        var text = new List<string>();
        foreach (var c in line.Section.Conditions)
        {
            var t = of(c.Raw);
            var where = "conditions" + (c.Not ? " " + g.Not : "");
            if (t.R.Note is { } index) notes.Add($"{where} '{c.Raw}' {index}");
            if (t.Throws) Drop($"{where} '{c.Raw}' {Miss(t)}; {StoulThrows}");
            else if (t.R.Form is { } fk)
            {
                forms.Add(new Named(SkyPatcherOverlay.FormPlace.Filter, where, c.Raw, fk, t.R.ByEditorId));
                if (ctx.ClassOf(fk) is { } cls && !SpidGrammar.IsOneOf(cls, g.ConditionTypes))
                    notes.Add($"{where} '{c.Raw}' names {FormIdToken.Of(fk)}, a record of type {RecordNaming.StripOverlay(cls.Name)}, and BOS matches a condition " +
                              $"only against a record of type {Prose(g.ConditionTypes)}, so this one never matches" +
                              (c.Not ? " and excludes no reference: BOS reads the line as if the condition were absent" : ""));
            }
            else if (t.Id is not null) notes.Add($"{where} '{c.Raw}' {Miss(t)}; BOS keeps it as that FormID, which matches no reference");
            else text.Add((c.Not ? g.Not.ToString() : "") + c.Raw);
        }
        if (text.Count > 0)
            notes.Add($"conditions {Quoted(text)} {(text.Count == 1 ? "names" : "name")} no record, so BOS keeps {(text.Count == 1 ? "it" : "them")} as text and in the running game " +
                      $"matches {(text.Count == 1 ? "it" : "each")} against the cell's EditorID, a keyword of the cell's location or a keyword on the base, which is not read here");
        var head = line.Section.Name.Split(g.Conditional);
        if (head.Length > 2) notes.Add($"its section's header has {head.Length} '{g.Conditional}' parts; BOS reads the conditions between the first and second only");

        var f = line.Fields;
        bool swaps = BosGrammar.Swaps(line.Section.Kind);
        var b = of(f[0]);
        if (b.R.Note is { } bIndex) notes.Add($"base '{f[0]}' {bIndex}");
        if (b.Throws) Drop($"base '{f[0]}' {Miss(b)}; {StoulThrows}");
        else if (b.Id is { } baseId)
        {
            if (b.R.Form is { } fk)
            {
                forms.Add(new Named(SkyPatcherOverlay.FormPlace.Target, "base", f[0], fk, b.R.ByEditorId));
                if (ctx.ClassOf(fk) is { } cls && !g.BaseCanApply(line.Section.Kind, cls))
                    notes.Add($"base '{f[0]}' names {FormIdToken.Of(fk)}, a record of type {RecordNaming.StripOverlay(cls.Name)}, which " +
                              $"{(swaps ? "no reference has as its base" : "is neither a reference nor any reference's base")}; BOS keeps the line under it, and it applies to no reference");
            }
            else notes.Add($"base '{f[0]}' {Miss(b)}; BOS keeps the line under that FormID, and it applies to no reference");
            bases.Add((baseId, f[0]));
            if (swaps) Swap(baseId);
            else if (!g.PropertyNames.Any(f[1].Contains))
                notes.Add($"its properties field '{f[1]}' names none of {Prose(g.PropertyNames)}, so BOS sets no property from it and the line never applies");
        }
        else if (swaps && f[0].Contains(g.Item)) BaseSet();
        else Drop($"base '{f[0]}' {Miss(b) ?? "names nothing"}; {Log("BASE formID not found")}");
        return new LineRead(forms, notes, dropped, bases);

        // One base: a swap or a swap set.
        void Swap(uint baseId)
        {
            var s = f[1];
            if (!s.Contains(g.Item))
            {
                var t = of(s);
                if (t.R.Note is { } index) notes.Add($"swap '{s}' {index}");
                if (t.Throws) { Drop($"swap '{s}' {Miss(t)}; {StoulThrows}"); return; }
                if (t.Id is not { } swapId) { Drop($"swap '{s}' {Miss(t) ?? "names nothing"}; {Log("SWAP formID not found")}"); return; }
                if (t.R.Form is { } fk) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Value, "swap", s, fk, t.R.ByEditorId));
                else notes.Add($"swap '{s}' {Miss(t)}; BOS keeps the line, and at a reference it finds no object there and tries an earlier line");
                var props = f.Count > 2 ? f[2] : "";
                if (swapId == baseId && !g.Set(props)) Drop($"its swap names the base itself and it sets no properties; {Log("BASE formID == SWAP formID")}");
                return;
            }
            int kept = Members(s, "swap", "swap set");
            if (kept == 0)
                Drop($"no member of its swap set '{s}' names a form{(s.Contains('(') ? " (the field holds a transform; a swap line is base|swap|properties|chance, so BOS reads this field as the swap)" : "")}; {Log("SWAP formID not found")}");
        }

        // A base set: BOS reads one only when the whole base names nothing and holds ','.
        void BaseSet()
        {
            var baseIds = new List<(uint, string)>();
            foreach (var m in f[0].Split(g.Item))
            {
                var t = of(m);
                if (t.R.Note is { } index) notes.Add($"base member '{m}' {index}");
                if (t.Throws) { Drop($"base member '{m}' {Miss(t)}; {StoulThrows}"); return; }
                if (t.Id is { } id)
                {
                    if (t.R.Form is { } fk) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Target, "base set", m, fk, t.R.ByEditorId));
                    else notes.Add($"base member '{m}' {Miss(t)}; BOS keeps it as that FormID, and it applies to no reference");
                    baseIds.Add((id, m));
                }
                else notes.Add($"base member '{m}' {Miss(t) ?? "names nothing"}; BOS logs 'failed to process' and leaves it out of the base set");
            }
            if (baseIds.Count == 0) { Drop($"no member of its base set '{f[0]}' names a form; {Log("BASE formID not found")}"); return; }
            int kept = f[1].Contains(g.Item) ? Members(f[1], "swap", "swap set") : One(f[1]);
            if (kept == 0) { Drop($"its swap '{f[1]}' names no form; {Log("SWAP formID set not found")}"); return; }
            if (kept != 1 && kept < baseIds.Count) { Drop($"its swap set keeps {kept} form(s) for {baseIds.Count} bases; {Log("SWAP formIDSet.size() must be 1 OR equal/greater than BASE formIDSet.size()")}"); return; }
            bases.AddRange(baseIds);
            if (kept > 1) notes.Add("BOS gives each base of the set a different member of the swap set, picked at random once at load");
        }

        int One(string s)
        {
            var t = of(s);
            if (t.R.Note is { } index) notes.Add($"swap '{s}' {index}");
            if (t.R.Form is { } fk) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Value, "swap", s, fk, t.R.ByEditorId));
            else if (t.Id is not null) notes.Add($"swap '{s}' {Miss(t)}; BOS keeps it as that FormID, and at a reference it finds no object there");
            return t.Id is null ? 0 : 1;
        }

        // Each member of a set: one naming nothing leaves the set, the rest stand.
        int Members(string s, string where, string set)
        {
            int kept = 0;
            var ids = new HashSet<uint>();
            foreach (var m in s.Split(g.Item))
            {
                var t = of(m);
                if (t.R.Note is { } index) notes.Add($"{where} member '{m}' {index}");
                if (t.Throws) { Drop($"{where} member '{m}' {Miss(t)}; {StoulThrows}"); return -1; }
                if (t.Id is { } id)
                {
                    if (t.R.Form is { } fk) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Value, where, m, fk, t.R.ByEditorId));
                    else notes.Add($"{where} member '{m}' {Miss(t)}; BOS keeps it as that FormID, and at a reference it finds no object there");
                    if (ids.Add(id)) kept++;
                }
                else notes.Add($"{where} member '{m}' {Miss(t) ?? "names nothing"}; BOS logs 'failed to process' and leaves it out of the {set}");
            }
            return kept;
        }
    }

    /// <summary>Why a token names nothing, as a clause the line's own words follow.</summary>
    static string? Miss(Taken t) => t.R.Miss?.TrimEnd('.');

    static string Quoted(IEnumerable<string> xs) => string.Join(", ", xs.Select(x => "'" + x + "'"));

    static string Prose(IReadOnlyList<string> xs) => xs.Count <= 1 ? string.Concat(xs) : string.Join(", ", xs.Take(xs.Count - 1)) + " or " + xs[^1];

    // ---- BOS's order --------------------------------------------------------------------

    /// <summary>A kept line's place in BOS's order for one base: its map, its place among that base's lines of the map
    /// (BOS tries them from the last back), and for a conditional map its condition set's key and that set's place among
    /// the base's sets (tried from the last key, in byte order, back).</summary>
    public sealed record Place(BosKind Kind, string Base, int Index, int Total, string? ConditionId, int SetIndex, int Sets);

    /// <summary>Every kept line's places, by (file index, line index): files in BOS's order, each file's lines by
    /// section (first appearance) then line, filed per base and map as BOS files them.</summary>
    public static IReadOnlyDictionary<(int File, int Line), IReadOnlyList<Place>> Places(
        IReadOnlyList<(IReadOnlyList<Line> Lines, IReadOnlyList<LineRead?> Reads)> files)
    {
        var entries = new List<(int File, int Line, BosKind Kind, uint Base, string Token, string Set)>();
        for (int fi = 0; fi < files.Count; fi++)
        {
            var (lines, reads) = files[fi];
            foreach (var li in Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].Section.Order).ThenBy(i => lines[i].Number))
                if (reads[li] is { Dropped: null } r)
                    foreach (var (id, token) in r.Bases) entries.Add((fi, li, lines[li].Section.Kind, id, token, lines[li].Section.ConditionId));
        }
        var places = new Dictionary<(int, int), List<Place>>();
        foreach (var byBase in entries.GroupBy(e => (e.Kind, e.Base)))
        {
            var sets = byBase.Select(e => e.Set).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            foreach (var bySet in byBase.GroupBy(e => e.Set))
            {
                var list = bySet.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    bool cond = e.Kind is BosKind.ConditionalForms or BosKind.ConditionalProperties;
                    if (!places.TryGetValue((e.File, e.Line), out var at)) places[(e.File, e.Line)] = at = new();
                    at.Add(new Place(e.Kind, e.Token, i + 1, list.Count, cond ? e.Set : null, sets.IndexOf(e.Set) + 1, sets.Count));
                }
            }
        }
        return places.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Place>)kv.Value);
    }
}

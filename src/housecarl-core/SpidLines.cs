using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>SPID's <c>_DISTR.ini</c> lines read as SPID 7.3 reads them (rules and sources:
/// docs/architecture/spid-layer.md): the unnamed section only, the comment leads, the value to the line's end,
/// <c>sanitize()</c>, the key kinds and the '|' sections, then every form a line names resolved through the one target
/// resolver under SPID's row and labelled target, filter or value, with what SPID does where a name finds nothing.
/// Filters are not evaluated against NPCs, and an outfit or leveled list is not followed to what it holds.</summary>
public static class SpidLines
{
    /// <summary>Which list of a field a token sits in: the plain list (OR), the <c>-</c> list (NOT), a <c>+</c> group
    /// (ALL), or a <c>*</c> string (part of a name).</summary>
    public enum ListKind { Plain, Not, All, Partial }

    /// <summary>One token as written, after sanitize: its field, its list, and for a <c>+</c> part the group it belongs to.</summary>
    public sealed record Token(SpidField Field, ListKind List, string Raw, int Group);

    /// <summary>One line of a file as SPID splits it. <see cref="Skipped"/> says why SPID reads no entry from it (null
    /// when it reads one); <see cref="Notes"/> are what SPID changes on the way (sanitize, a dropped <c>Final</c>).</summary>
    public sealed record Line(int Number, string Raw, string Key, SpidLineKind Kind, string? Type, bool Final, bool Global,
                              IReadOnlyList<string> Sections, IReadOnlyList<Token> Tokens, string? Skipped, IReadOnlyList<string> Notes);

    // ---- splitting a file ---------------------------------------------------------------

    /// <summary>Every key line of one file's text in file order, read or skipped, as SPID's reader meets it.</summary>
    public static IReadOnlyList<Line> Parse(string text, SpidGrammar g)
    {
        var lines = new List<Line>();
        var physical = text.Split('\n');
        string? section = null;   // null: the unnamed section, the only one SPID reads
        for (int i = 0; i < physical.Length; i++)
        {
            var raw = physical[i].TrimEnd('\r');
            var t = raw.Trim();
            if (t.Length == 0 || g.CommentLeads.Contains(t[0])) continue;
            if (t[0] == '[' && t.IndexOf(']') is var close and > 0)
            {
                var name = t[1..close].Trim();
                section = name.Length == 0 ? null : name;
                continue;
            }
            int eq = t.IndexOf(g.Assign);
            if (eq < 0)
            {
                lines.Add(Skip(i + 1, raw, t, $"has no '{g.Assign}', so SPID reads no entry from it"));
                continue;
            }
            var key = t[..eq].Trim();
            if (section is not null)
            {
                lines.Add(Skip(i + 1, raw, key, $"sits under [{section}], and SPID reads only the lines before the first section header"));
                continue;
            }
            lines.Add(ParseLine(i + 1, raw, key, t[(eq + 1)..].Trim(), g));
        }
        return lines;
    }

    static Line Skip(int n, string raw, string key, string why)
        => new(n, raw, key, SpidLineKind.Regular, null, false, false, Array.Empty<string>(), Array.Empty<Token>(), why, Array.Empty<string>());

    static Line ParseLine(int n, string raw, string key, string value, SpidGrammar g)
    {
        var notes = new List<string>();
        var (kind, type, final, global) = Classify(key, g);
        if (type is null && kind != SpidLineKind.ExclusiveGroup)
            return Skip(n, raw, key, $"has key '{key}', which is not one SPID takes (a type is one of {string.Join(", ", g.Types.Select(x => x.Key))}, " +
                                     "spelt in that case), so SPID skips the line");
        if (final && !g.FinalTypes.Contains(type!))
        {
            notes.Add($"'{g.FinalPrefix}' applies to {string.Join(", ", g.FinalTypes)} lines only, so SPID drops it and reads this as a {type} line");
            final = false;
        }
        var clean = Sanitize(value, g);
        if (clean != value) notes.Add($"SPID reads the value as '{clean}' and writes that back into this file when the game starts");

        var fields = g.Kind(kind).Fields;
        var sections = clean.Split(g.Sections);
        Line Out(string? skipped, IReadOnlyList<Token> tokens)
            => new(n, raw, key, kind, type, final, global, sections, tokens, skipped, notes);
        if (sections.Length > fields.Count)
            return Out($"has {sections.Length} '{g.Sections}' sections, more than the {fields.Count} a {KindName(kind)} line takes, so SPID skips it", Array.Empty<Token>());

        var tokens = new List<Token>();
        int group = 0;
        for (int f = 0; f < sections.Length; f++)
        {
            var field = fields[f];
            var sec = sections[f];
            bool set = Set(sec, g);
            switch (field)
            {
                case SpidField.Value:
                    if (!set) return Out("names no form to hand out, so SPID skips it", tokens);
                    tokens.Add(new Token(field, ListKind.Plain, sec, 0));
                    break;
                case SpidField.Members or SpidField.Parents:
                    if (!set) return Out($"names no {(field == SpidField.Members ? "member" : "parent")} form, so SPID skips it", tokens);
                    foreach (var item in sec.Split(g.Item))
                    {
                        if (item.Length == 0) return Out($"has an empty item in its {FieldName(field)}, so SPID skips it", tokens);
                        tokens.Add(field == SpidField.Members && item[0] == g.Not
                            ? new Token(field, ListKind.Not, item[1..], 0) : new Token(field, ListKind.Plain, item, 0));
                    }
                    break;
                case SpidField.Strings or SpidField.Forms when set:
                    foreach (var item in sec.Split(g.Item))
                    {
                        // SPID reads each term's first character, so an empty term throws and the line is skipped.
                        if (item.Length == 0) return Out($"has an empty {FieldName(field)} term, so SPID skips it", tokens);
                        if (item.Contains(g.All))
                        {
                            group++;
                            foreach (var part in item.Split(g.All))
                            {
                                if (part.Length == 0) return Out($"has an empty '{g.All}' part in its {FieldName(field)}, so SPID skips it", tokens);
                                tokens.Add(new Token(field, ListKind.All, part, group));
                            }
                        }
                        else if (item[0] == g.Not) tokens.Add(new Token(field, ListKind.Not, item[1..], 0));
                        else if (field == SpidField.Strings && item[0] == g.Partial) tokens.Add(new Token(field, ListKind.Partial, item[1..], 0));
                        else tokens.Add(new Token(field, ListKind.Plain, item, 0));
                    }
                    break;
            }
        }
        return Out(null, tokens);
    }

    /// <summary>A key's kind, its type, and its <c>Final</c> and <c>Global</c> prefixes, in the order SPID tries the
    /// shapes; a null type for a key no shape takes.</summary>
    static (SpidLineKind Kind, string? Type, bool Final, bool Global) Classify(string key, SpidGrammar g)
    {
        var group = g.Kind(SpidLineKind.ExclusiveGroup);
        if (key == group.Key) return (SpidLineKind.ExclusiveGroup, null, false, false);
        var linked = g.Kind(SpidLineKind.Linked);
        var death = g.Kind(SpidLineKind.Death).Prefix!;
        var k = key;
        bool global = linked.Global is { } gl && k.StartsWith(gl, StringComparison.Ordinal);
        if (global) k = k[linked.Global!.Length..];
        bool Strip(string prefix)
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) return false;
            k = k[prefix.Length..];
            return true;
        }
        if (Strip(linked.Prefix!))
        {
            bool lf = Strip(g.FinalPrefix);
            Strip(death);   // a linked death line links on the death event; its fields are a linked line's
            return (SpidLineKind.Linked, g.Type(k)?.Key, lf, global);
        }
        if (global) return (SpidLineKind.Regular, null, false, true);   // Global is a Linked prefix only
        bool final = Strip(g.FinalPrefix);
        bool isDeath = Strip(death);
        return (isDeath ? SpidLineKind.Death : SpidLineKind.Regular, g.Type(k)?.Key, final, false);
    }

    /// <summary>Whether a section is set: SPID takes an empty section or the unset word, in any case, as the whole
    /// section only, so the word inside a list is a name like any other.</summary>
    static bool Set(string section, SpidGrammar g)
        => section.Length > 0 && !section.Equals(g.Unset, StringComparison.OrdinalIgnoreCase);

    static readonly Regex Separators = new(@"\s*([|,])\s*", RegexOptions.CultureInvariant);
    static readonly Regex PaddedHex = new(@"\b00+([0-9a-fA-F]{1,6})\b", RegexOptions.CultureInvariant);
    static readonly Regex PaddedPrefixedHex = new(@"(0x00+)([0-9a-fA-F]+)", RegexOptions.CultureInvariant);

    /// <summary>SPID's <c>sanitize()</c>: the first <c>" - "</c> becomes '~' when the value holds none, spaces around
    /// '|' and ',' go, and zero-padded hex gains a <c>0x</c> or loses its padding.</summary>
    public static string Sanitize(string value, SpidGrammar g)
    {
        var v = value;
        if (!v.Contains('~') && v.IndexOf(" - ", StringComparison.Ordinal) is var dash and >= 0)
            v = v[..dash] + "~" + v[(dash + 3)..];
        v = Separators.Replace(v, "$1");
        v = PaddedHex.Replace(v, "0x$1");
        return PaddedPrefixedHex.Replace(v, "0x$2");
    }

    /// <summary>A field's name as a line note spells it.</summary>
    public static string FieldName(SpidField f) => f switch
    {
        SpidField.Value => "value",
        SpidField.Strings => "string filter",
        SpidField.Forms => "form filter",
        SpidField.Parents => "linked parents",
        SpidField.Members => "group members",
        _ => f.ToString(),
    };

    static string KindName(SpidLineKind k) => k switch
    {
        SpidLineKind.ExclusiveGroup => "ExclusiveGroup",
        SpidLineKind.Linked => "Linked",
        SpidLineKind.Death => "Death",
        _ => "regular",
    };

    // ---- what a line names --------------------------------------------------------------

    /// <summary>One form a line names: where (target, filter or value), in which field and list, the token as written,
    /// the record, and whether it was named by EditorID.</summary>
    public sealed record Named(SkyPatcherOverlay.FormPlace Place, string Where, string Token, FormKey Form, bool ByEditorId);

    /// <summary>What one line names and what SPID does with it: every form it names that resolves, every plugin it names
    /// as a term (present in the order), the notes its tokens owe, why SPID drops the line (null when it keeps it), and
    /// the class of the record its value names.</summary>
    public sealed record LineRead(IReadOnlyList<Named> Forms, IReadOnlyList<string> PluginTerms, IReadOnlyList<string> Notes,
                                  string? Dropped, Type? ValueClass);

    /// <summary>The lookups a read needs: the call's target resolver, and the class of a resolved record off the order.</summary>
    public sealed record Context(SkyPatcherOverlay.IFormResolver Resolver, Func<FormKey, Type?> ClassOf, DistributorFormat Format)
    {
        internal SpidGrammar G => Format.Lines!;
        internal TargetSpelling S => Format.Targets;
    }

    /// <summary>Read every line of <paramref name="lines"/>: every form-position token of every line first, then every
    /// string token, so the call's EditorID table walks every type once before the narrower string lookups use it.
    /// A skipped line reads as null.</summary>
    public static IReadOnlyList<LineRead?> ReadAll(IReadOnlyList<Line> lines, Context ctx)
    {
        var parts = lines.Select(l => l.Skipped is null ? ReadForms(l, ctx) : null).ToList();
        return lines.Select((l, i) => parts[i] is { } p ? Merge(p, ReadStrings(l, ctx)) : null).ToList();
    }

    /// <summary>Read one line.</summary>
    public static LineRead? Read(Line line, Context ctx)
        => line.Skipped is null ? Merge(ReadForms(line, ctx), ReadStrings(line, ctx)) : null;

    static LineRead Merge(LineRead a, (List<Named> Forms, List<string> Notes) s)
        => a with { Forms = a.Forms.Concat(s.Forms).ToList(), Notes = a.Notes.Concat(s.Notes).ToList() };

    static LineRead ReadForms(Line line, Context ctx)
    {
        var forms = new List<Named>();
        var plugins = new List<string>();
        var notes = new List<string>();
        string? dropped = null;
        Type? valueClass = null;
        var g = ctx.G;
        void Drop(string why) => dropped ??= why;
        int created = -1;

        foreach (var t in line.Tokens.Where(t => t.Field == SpidField.Value))
        {
            var r = Resolve(t, ctx);
            if (r.Note is { } index) notes.Add($"value '{t.Raw}' {index}");
            if (r.Form is { } fk)
            {
                forms.Add(new Named(SkyPatcherOverlay.FormPlace.Value, line.Key, t.Raw, fk, r.ByEditorId));
                valueClass = ctx.ClassOf(fk);
            }
            else if (r.Plugin is not null) Drop($"value '{t.Raw}' names a plugin, not a form to hand out, so the line hands nothing out");
            else if (r.Miss is { } miss)
            {
                var type = g.Type(line.Type!)!;
                if (!r.ByEditorId) Drop($"value '{t.Raw}' {miss}; SPID drops the line");
                else switch (type.MissingValue)
                {
                    case SpidMissingValue.CreatesKeyword:
                        created = notes.Count;
                        notes.Add($"value '{t.Raw}' {miss}, so SPID creates a keyword of that name");
                        break;
                    case SpidMissingValue.CreatesKeywordHandsNothing:
                        Drop($"value '{t.Raw}' {miss}, so SPID creates a keyword of that name, which a {type.Key} line does not hand out: the line hands nothing out");
                        break;
                    default:
                        Drop($"value '{t.Raw}' {miss}, so SPID drops the line");
                        break;
                }
            }
        }

        // Form filters: a plain or '-' token that names nothing, or a record SPID does not take as a filter, is skipped,
        // unless every token of its list is, which drops the line; one '+' part that names nothing drops it.
        foreach (var list in new[] { ListKind.Plain, ListKind.Not, ListKind.All })
        {
            var toks = line.Tokens.Where(t => t.Field == SpidField.Forms && t.List == list).ToList();
            if (toks.Count == 0) continue;
            int failed = 0;
            var missNotes = new List<string>();
            foreach (var t in toks)
            {
                var r = Resolve(t, ctx);
                var where = FieldName(SpidField.Forms) + ListMark(list, g);
                if (r.Note is { } index) notes.Add($"{where} '{t.Raw}' {index}");
                string? why = r.Miss;
                if (r.Form is { } fk)
                {
                    var cls = ctx.ClassOf(fk);
                    bool target = list != ListKind.Not && SpidGrammar.IsOneOf(cls, g.TargetTypes);
                    forms.Add(new Named(target ? SkyPatcherOverlay.FormPlace.Target : SkyPatcherOverlay.FormPlace.Filter, where, t.Raw, fk, r.ByEditorId));
                    if (list != ListKind.All && !SpidGrammar.IsOneOf(cls, g.FilterTypes))
                        why = $"names {FormIdToken.Of(fk)}, a {RecordName(cls)} record, which SPID does not take as a form filter";
                }
                else if (r.Plugin is { } p)
                {
                    if (r.PluginPresent) plugins.Add(p); else why = r.Miss;
                }
                if (why is null) continue;
                failed++;
                missNotes.Add($"{where} '{t.Raw}' {why}");
            }
            if (failed == 0) continue;
            if (list == ListKind.All)
                Drop(missNotes[0] + $", and a '{g.All}' group needs every part, so SPID drops the line");
            else if (failed == toks.Count)
                Drop(string.Join("; ", missNotes) + $"; every {(list == ListKind.Not ? $"'{g.Not}' " : "")}form filter of the line names nothing SPID can use, so SPID drops the line");
            else
                notes.AddRange(missNotes.Select(m => m + (list == ListKind.Not
                    ? "; SPID skips it, so the line excludes fewer NPCs"
                    : "; SPID skips it, so the line reaches more NPCs")));
        }

        // Linked parents and group members: forms the line names, labelled filter; what SPID does with one that names nothing is not read.
        foreach (var t in line.Tokens.Where(t => t.Field is SpidField.Parents or SpidField.Members))
        {
            var r = Resolve(t, ctx);
            var where = FieldName(t.Field) + ListMark(t.List, g);
            if (r.Note is { } index) notes.Add($"{where} '{t.Raw}' {index}");
            if (r.Form is { } fk) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Filter, where, t.Raw, fk, r.ByEditorId));
            else if (r.Plugin is { } p && r.PluginPresent) plugins.Add(p);
            else if (r.Miss is { } miss) notes.Add($"{where} '{t.Raw}' {miss}; what SPID does with such a {(t.Field == SpidField.Members ? "member" : "parent")} is not read here");
        }
        // The keyword is created at the value's lookup, before the filters can drop the line.
        if (created >= 0) notes[created] += dropped is null ? " and hands it out" : ", which the dropped line hands to no one";
        return new LineRead(forms, plugins, notes, dropped, valueClass);
    }

    /// <summary>String filters: a token that is an NPC's or a keyword's EditorID names that record (an NPC as target,
    /// unless excluded); any other is matched in the running game by name, which is not read here.</summary>
    static (List<Named> Forms, List<string> Notes) ReadStrings(Line line, Context ctx)
    {
        var forms = new List<Named>();
        var names = new List<string>();
        var partial = new List<string>();
        var g = ctx.G;
        foreach (var t in line.Tokens.Where(t => t.Field == SpidField.Strings))
        {
            if (t.List == ListKind.Partial) { partial.Add(g.Partial + t.Raw); continue; }
            var where = FieldName(SpidField.Strings) + ListMark(t.List, g);
            var hit = g.StringLookups.Select(scope => (scope, fk: ctx.Resolver.ResolveEditorId(t.Raw, scope))).FirstOrDefault(x => x.fk is not null);
            if (hit.fk is { } fk)
            {
                bool target = t.List != ListKind.Not && g.TargetTypes.Contains(hit.scope);
                forms.Add(new Named(target ? SkyPatcherOverlay.FormPlace.Target : SkyPatcherOverlay.FormPlace.Filter, where, t.Raw, fk, true));
            }
            else names.Add((t.List == ListKind.Not ? g.Not.ToString() : "") + t.Raw);
        }
        var notes = new List<string>();
        if (names.Count > 0)
            notes.Add($"string filter {Quoted(names)} {(names.Count == 1 ? "is" : "are")} no {string.Join(" or ", g.StringLookups.Select(Spoken))} EditorID, so " +
                      $"{(names.Count == 1 ? "it matches" : "they match")} by name (an actor's display name) in the running game, which is not read here");
        if (partial.Count > 0)
            notes.Add($"string filter {Quoted(partial)} {(partial.Count == 1 ? "matches" : "match")} part of a name in the running game, which is not read here");
        return (forms, notes);
    }

    /// <summary>A record name as prose says it: NPC for Npc, the rest in lower case.</summary>
    static string Spoken(string record) => record == "Npc" ? "NPC" : record.ToLowerInvariant();

    static string Quoted(IEnumerable<string> xs) => string.Join(", ", xs.Select(x => "'" + x + "'"));

    static string ListMark(ListKind l, SpidGrammar g) => l switch
    {
        ListKind.Not => " " + g.Not,
        ListKind.All => " " + g.All,
        _ => "",
    };

    /// <summary>A record class as a line names it.</summary>
    static string RecordName(Type? cls) => cls is null ? "unknown" : RecordNaming.StripOverlay(cls.Name);

    /// <summary>One token resolved: the record it names, the plugin it names as a term (and whether the order holds it),
    /// why it names nothing, a note it owes even when it resolves (a load index written in), and whether it was a name.</summary>
    sealed record Resolved(FormKey? Form, string? Plugin, bool PluginPresent, string? Miss, string? Note, bool ByEditorId);

    static Resolved Resolve(Token t, Context ctx)
    {
        var read = RuntimeTargets.Read(t.Raw, ctx.S);
        var fk = RuntimeTargets.Resolve(read, null, ctx.Resolver, ctx.S);
        // A resolved address still owes the load-index note; a resolved name or runtime FormID owes nothing.
        var report = fk is null || read.Kind == RuntimeTargets.TargetKind.Address ? RuntimeTargets.Report(read, null, ctx.Resolver, "SPID", ctx.S) : null;
        bool byName = read.Kind == RuntimeTargets.TargetKind.EditorId;
        if (read.Kind == RuntimeTargets.TargetKind.PluginName)
        {
            bool present = ctx.Resolver.PluginPresent(read.Raw);
            return new Resolved(null, read.Raw, present, present ? null : report, null, false);
        }
        if (fk is not null) return new Resolved(fk, null, false, null, report, byName);
        return new Resolved(null, null, false, report ?? "names nothing", null, byName);
    }

    // ---- first wins ----------------------------------------------------------------------

    /// <summary>A first-wins line's place: its type, its kind's group (regular, death or linked), its 1-based place and
    /// the group's count, in SPID's order.</summary>
    public sealed record Place(string Type, string Group, int Index, int Total);

    /// <summary>Each first-wins line's place in its type's order, as SPID builds the order: files in the given order,
    /// within a file keys case-insensitively then line order, a <c>Form</c> line after every typed line of every file
    /// (in the type its value is). Skipped and dropped lines take no place. Keyed by (file index, line index).</summary>
    public static Dictionary<(int File, int Line), Place> Places(IReadOnlyList<(IReadOnlyList<Line> Lines, IReadOnlyList<LineRead?> Reads)> files, SpidGrammar g)
    {
        var groups = new Dictionary<(string Type, string Group), (List<(int, int)> Typed, List<(int, int)> Form)>();
        for (int f = 0; f < files.Count; f++)
        {
            var (lines, reads) = files[f];
            var order = Enumerable.Range(0, lines.Count)
                .OrderBy(i => lines[i].Key, StringComparer.OrdinalIgnoreCase).ThenBy(i => lines[i].Number);
            foreach (var i in order)
            {
                var l = lines[i];
                if (l.Skipped is not null || l.Type is null || reads[i] is not { Dropped: null } read) continue;
                var type = g.Type(l.Type)!;
                bool inferred = type.Runs == SpidType.Inferred;
                if (inferred)
                    type = g.Types.FirstOrDefault(x => x.Record is { } rec && SpidGrammar.IsOneOf(read.ValueClass, new[] { rec })) ?? type;
                if (type.Conflict != SpidConflict.FirstWins) continue;
                var key = (type.Key, l.Kind.ToString());
                if (!groups.TryGetValue(key, out var lists)) groups[key] = lists = (new(), new());
                (inferred ? lists.Form : lists.Typed).Add((f, i));
            }
        }
        var places = new Dictionary<(int, int), Place>();
        foreach (var ((type, group), (typed, form)) in groups)
        {
            var all = typed.Concat(form).ToList();
            for (int k = 0; k < all.Count; k++) places[all[k]] = new Place(type, group, k + 1, all.Count);
        }
        return places;
    }
}

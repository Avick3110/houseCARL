using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>KID's <c>_KID.ini</c> lines read as KID 4.0.6 and 4.1.0 read them (rules and sources:
/// docs/architecture/kid-layer.md): the unnamed section only, the comment leads, the value to the line's end, the five
/// '|' sections untrimmed, the type strings exact, the filter entries and their '+' groups, then every form a line
/// names resolved through the one target resolver under KID's row and labelled target, filter or value, with what KID
/// does where a name finds nothing, by field. Filters, traits and chance are not evaluated against items, and a form
/// list is not followed to what it holds.</summary>
public static class KidLines
{
    /// <summary>Which section a token sits in: the filters, or an <c>ExclusiveGroup</c>'s members; the keyword section is <see cref="Line.Keyword"/>.</summary>
    public enum KidField { Filters, Members }

    /// <summary>One token as written: its section, whether a leading '-' excludes it, whether a leading '*' makes it a
    /// partial string, the '+' group it sits in (-1 for the plain list), and the term with its modifiers taken off.</summary>
    public sealed record Token(KidField Field, bool Not, bool Partial, int Group, string Raw)
    {
        /// <summary>The term as written, modifiers included.</summary>
        public string Written(KidGrammar g) => (Not ? g.Not.ToString() : "") + (Partial ? g.Partial.ToString() : "") + Raw;
    }

    /// <summary>One line of a file as KID splits it. <see cref="IsGroup"/> marks an <c>ExclusiveGroup</c> line;
    /// <see cref="Keyword"/> is the keyword section (a group's name); <see cref="Skipped"/> says why KID reads no entry
    /// from it (null when it reads one); <see cref="Notes"/> are what KID ignores on the way.</summary>
    public sealed record Line(int Number, string Raw, string Key, bool IsGroup, string? Type, string Keyword,
                              IReadOnlyList<Token> Tokens, string? Skipped, IReadOnlyList<string> Notes);

    const string FailedToParse = "so KID fails to parse the entry and logs 'Failed to parse entry'";

    // ---- splitting a file ---------------------------------------------------------------

    /// <summary>Every key line of one file's text in file order, read or skipped, as KID's reader meets it.</summary>
    public static IReadOnlyList<Line> Parse(string text, KidGrammar g)
    {
        var lines = new List<Line>();
        var physical = text.Split('\n');
        string? section = null;   // null: the unnamed section, the only one KID reads
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
                lines.Add(Skip(i + 1, raw, t, false, $"has no '{g.Assign}', so KID reads no entry from it"));
                continue;
            }
            var key = t[..eq].Trim();
            if (section is not null)
            {
                lines.Add(Skip(i + 1, raw, key, false, $"sits under [{section}], and KID reads only the unnamed section: the lines before the first named header and any after an empty '[]'"));
                continue;
            }
            var value = t[(eq + 1)..].Trim();
            lines.Add(key == g.GroupKey ? ParseGroup(i + 1, raw, key, value, g) : ParseLine(i + 1, raw, key, value, g));
        }
        return lines;
    }

    static Line Skip(int n, string raw, string key, bool group, string why)
        => new(n, raw, key, group, null, "", Array.Empty<Token>(), why, Array.Empty<string>());

    static Line ParseLine(int n, string raw, string key, string value, KidGrammar g)
    {
        var sections = value.Split(g.Sections);
        var typeText = sections.Length > 1 ? sections[1] : "";
        if (typeText.Length == 0 || g.Type(typeText) is not { } type)
        {
            var why = typeText.Length == 0 ? "has no type section"
                : g.Type(typeText.Trim()) is not null ? $"has type '{typeText}', and KID does not trim around '{g.Sections}', so it is not one of KID's types"
                : $"has type '{typeText}', which is not one of KID's types ({string.Join(", ", g.Types.Select(x => x.Key))}, spelt exactly so)";
            return Skip(n, raw, key, false, why + ", so KID drops the line without a log");
        }
        var notes = new List<string>();
        var tokens = new List<Token>();
        if (Unreadable(sections[0], g) is { } bad) return Skip(n, raw, key, false, $"names keyword '{sections[0]}', {bad}, {FailedToParse}");

        if (sections.Length > 2 && g.Set(sections[2]))
        {
            int group = 0;
            foreach (var item in sections[2].Split(g.Item))
            {
                var entry = item.Trim();
                if (!g.Set(entry)) continue;
                if (entry.Contains(g.All))
                {
                    foreach (var part in entry.Split(g.All))
                        if (part.Trim() is { Length: > 0 } p) tokens.Add(Term(p, group, g));
                    group++;
                }
                else tokens.Add(Term(entry, -1, g));
            }
            if (tokens.FirstOrDefault(x => !x.Partial && Unreadable(x.Raw, g) is not null) is { } t)
                return Skip(n, raw, key, false, $"has filter '{t.Raw}', {Unreadable(t.Raw, g)}, {FailedToParse}");
        }
        if (sections.Length > 3 && g.Set(sections[3]) && !type.Traits)
            notes.Add($"a {type.Key} line takes no traits, so KID ignores '{sections[3]}'");
        if (sections.Length > 4 && g.Set(sections[4]) && !ReadsAsNumber(sections[4]))
            return Skip(n, raw, key, false, $"has chance '{sections[4]}', which is not a number, {FailedToParse}");
        if (sections.Length > 5)
            notes.Add($"has {sections.Length} '{g.Sections}' sections; KID reads the first five and ignores the rest");
        return new Line(n, raw, key, false, type.Key, sections[0], tokens, null, notes);
    }

    /// <summary>One filter entry or '+' part: a leading '-' excludes, then a leading '*' makes a partial string.</summary>
    static Token Term(string entry, int group, KidGrammar g)
    {
        bool not = entry[0] == g.Not;
        var body = not ? entry[1..] : entry;
        bool partial = body.Length > 0 && body[0] == g.Partial;
        return new Token(KidField.Filters, not, partial, group, partial ? body[1..] : body);
    }

    static Line ParseGroup(int n, string raw, string key, string value, KidGrammar g)
    {
        var sections = value.Split(g.Sections);
        if (sections.Length < 2) return Skip(n, raw, key, true, "has no members section, so KID ignores it and logs a warning");
        if (!g.Set(sections[1])) return Skip(n, raw, key, true, "names no members, so KID ignores it and logs a warning");
        var tokens = new List<Token>();
        // KID splits the members untrimmed, so a space stays part of the name.
        foreach (var item in sections[1].Split(g.Item))
        {
            if (item.Length == 0) return Skip(n, raw, key, true, $"has an empty member, {FailedToParse}");
            bool not = item[0] == g.Not;
            var body = not ? item[1..] : item;
            if (Unreadable(body, g) is { } bad) return Skip(n, raw, key, true, $"has member '{body}', {bad}, {FailedToParse}");
            tokens.Add(new Token(KidField.Members, not, false, -1, body));
        }
        return new Line(n, raw, key, true, null, sections[0], tokens, null, Array.Empty<string>());
    }

    /// <summary>Why KID cannot read a term holding one '~' as a FormID (its hex part has no digit, or passes 32 bits),
    /// or null when it can or the term is not an address. KID reads a term with two or more '~' as a name.</summary>
    static string? Unreadable(string term, KidGrammar g)
    {
        int at = term.IndexOf('~');
        if (at < 0 || term.IndexOf('~', at + 1) >= 0) return null;
        return RuntimeTargets.ParseHex(term[..at]) is null ? "whose part before '~' is no hex FormID KID can read" : null;
    }

    /// <summary>Whether <c>std::stof</c> reads a number at the start of the text: leading space, a sign, then a digit,
    /// a point and a digit, or inf or nan; it stops at the first character after.</summary>
    public static bool ReadsAsNumber(string s)
    {
        var t = s.TrimStart().AsSpan();
        if (t.Length > 0 && t[0] is '+' or '-') t = t[1..];
        if (t.Length > 0 && char.IsAsciiDigit(t[0])) return true;
        if (t.Length > 1 && t[0] == '.' && char.IsAsciiDigit(t[1])) return true;
        return t.StartsWith("inf", StringComparison.OrdinalIgnoreCase) || t.StartsWith("nan", StringComparison.OrdinalIgnoreCase);
    }

    // ---- what a line names --------------------------------------------------------------

    /// <summary>One form a line names: where (target, filter or value), in which section and list, the token as written,
    /// the record, and whether it was named by EditorID.</summary>
    public sealed record Named(SkyPatcherOverlay.FormPlace Place, string Where, string Token, FormKey Form, bool ByEditorId);

    /// <summary>What one line names and what KID does with it: every form it names that resolves, every plugin it names
    /// as a term (present in the order), the notes its tokens owe, and why KID drops the line (null when it keeps it).</summary>
    public sealed record LineRead(IReadOnlyList<Named> Forms, IReadOnlyList<string> PluginTerms, IReadOnlyList<string> Notes, string? Dropped);

    /// <summary>The lookups a read needs: the call's target resolver, and the class of a resolved record off the order.</summary>
    public sealed record Context(SkyPatcherOverlay.IFormResolver Resolver, Func<FormKey, Type?> ClassOf, DistributorFormat Format)
    {
        internal KidGrammar G => Format.KidLines!;
        internal TargetSpelling S => Format.Targets;
    }

    /// <summary>Read every line of <paramref name="lines"/> as KID does: every filter and member token first (an
    /// every-type lookup, so the call's EditorID table walks every type once), then every line's keyword, creating the
    /// ones no keyword holds, before any filter is read, so a filter naming a keyword another line creates finds it. A
    /// skipped line reads as null.</summary>
    public static IReadOnlyList<LineRead?> ReadAll(IReadOnlyList<Line> lines, Context ctx)
    {
        // A token's read depends only on its text in this call, so each distinct one is resolved once; the maps die with the call.
        var tokens = new Dictionary<string, Resolved>(StringComparer.Ordinal);
        var names = new Dictionary<string, Keyword>(StringComparer.Ordinal);
        var resolved = lines.Select(l => l.Skipped is null
            ? l.Tokens.Select(t => t.Partial || (t.Field == KidField.Filters && IsModel(t.Raw, ctx.G)) ? null
                : tokens.TryGetValue(t.Raw, out var r) ? r : tokens[t.Raw] = Resolve(t.Raw, ctx)).ToList()
            : null).ToList();
        var keywords = lines.Select(l => l.Skipped is not null || l.IsGroup ? null
            : names.TryGetValue(l.Keyword, out var k) ? k : names[l.Keyword] = ResolveKeyword(l.Keyword, ctx)).ToList();
        // KID finds a created keyword by name in any case, so a later spelling in another case creates no second one.
        var firstSpelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < keywords.Count; i++)
            if (keywords[i] is { Created: true } ck && !firstSpelling.TryAdd(ck.Raw, ck.Raw) && firstSpelling[ck.Raw] != ck.Raw)
                keywords[i] = ck with { Created = false, CreatedAs = firstSpelling[ck.Raw] };
        var created = new HashSet<string>(keywords.Where(k => k is { Created: true }).Select(k => k!.Raw), StringComparer.OrdinalIgnoreCase);
        return lines.Select((l, i) => l.Skipped is not null ? null
            : l.IsGroup ? ReadGroup(l, resolved[i]!, created, ctx)
            : ReadLine(l, keywords[i]!, resolved[i]!, created, ctx)).ToList();
    }

    /// <summary>Whether a filter term is a model path: it ends in the row's model ending, in any case, as KID tests it
    /// after lower-casing the path.</summary>
    static bool IsModel(string term, KidGrammar g) => term.EndsWith(g.ModelEnding, StringComparison.OrdinalIgnoreCase);

    /// <summary>One token resolved: the record it names, the plugin it names as a term (and whether the order holds it),
    /// why it names nothing, a note it owes even when it resolves (a load index written in), and whether it was a name.</summary>
    sealed record Resolved(FormKey? Form, string? Plugin, bool PluginPresent, string? Miss, string? Note, bool ByEditorId);

    /// <summary>A token as KID's row reads it, with KID's own rule first: a term with two or more '~' is a name.</summary>
    static RuntimeTargets.TargetToken ReadToken(string raw, TargetSpelling s)
        => raw.IndexOf(s.Separator) is var at and >= 0 && raw.IndexOf(s.Separator, at + 1) >= 0 ? new RuntimeTargets.TargetToken(RuntimeTargets.TargetKind.EditorId, raw, null, null) : RuntimeTargets.Read(raw, s);

    static Resolved Resolve(string raw, Context ctx)
    {
        var read = ReadToken(raw, ctx.S);
        var fk = RuntimeTargets.Resolve(read, null, ctx.Resolver, ctx.S);
        // A resolved address still owes the load-index note; a resolved name or runtime FormID owes nothing.
        var report = fk is null || read.Kind == RuntimeTargets.TargetKind.Address ? RuntimeTargets.Report(read, null, ctx.Resolver, "KID", ctx.S) : null;
        bool byName = read.Kind == RuntimeTargets.TargetKind.EditorId;
        if (read.Kind == RuntimeTargets.TargetKind.PluginName)
        {
            bool present = ctx.Resolver.PluginPresent(read.Raw);
            return new Resolved(null, read.Raw, present, present ? null : report, null, false);
        }
        if (fk is not null) return new Resolved(fk, null, false, null, report, byName);
        return new Resolved(null, null, false, report ?? "names nothing", null, byName);
    }

    /// <summary>The keyword section as KID reads it: a FormID looked up as a keyword, or a name (a plugin name too) looked
    /// up among keywords and created when none holds it.</summary>
    sealed record Keyword(string Raw, FormKey? Form, bool ByEditorId, bool Created, string? Miss, string? Note, string? CreatedAs = null);

    static Keyword ResolveKeyword(string raw, Context ctx)
    {
        var read = ReadToken(raw, ctx.S);
        if (read.Kind is RuntimeTargets.TargetKind.Address or RuntimeTargets.TargetKind.RuntimeFormId)
        {
            var fk = RuntimeTargets.Resolve(read, null, ctx.Resolver, ctx.S);
            var report = RuntimeTargets.Report(read, null, ctx.Resolver, "KID", ctx.S);
            if (fk is null) return new Keyword(raw, null, false, false, report ?? "names nothing", null);
            var cls = ctx.ClassOf(fk.Value);
            var miss = SpidGrammar.IsOneOf(cls, new[] { ctx.G.KeywordRecord }) ? null : $"names {FormIdToken.Of(fk.Value)}, {RecordName(cls)} record, not a keyword";
            return new Keyword(raw, fk, false, false, miss, report);
        }
        if (raw.Length == 0) return new Keyword(raw, null, true, false, "is empty", null);
        var hit = ctx.Resolver.ResolveEditorId(raw, ctx.G.KeywordRecord);
        var note = ctx.Resolver.EditorIdNote(raw, ctx.G.KeywordRecord) ?? ctx.Resolver.EditorIdNotLookedUp(ctx.G.KeywordRecord);
        return new Keyword(raw, hit, true, hit is null, null, note);
    }

    static LineRead ReadLine(Line line, Keyword kw, IReadOnlyList<Resolved?> resolved, HashSet<string> created, Context ctx)
    {
        var g = ctx.G;
        var type = g.Type(line.Type!)!;
        var forms = new List<Named>();
        var plugins = new List<string>();
        var notes = new List<string>(line.Notes);
        string? dropped = null;
        void Drop(string why) => dropped ??= why;

        int createdNote = -1;
        if (kw.Note is { } kn) notes.Add($"keyword '{kw.Raw}' {kn}");
        if (kw.Form is { } kf) forms.Add(new Named(SkyPatcherOverlay.FormPlace.Value, "keyword", kw.Raw, kf, kw.ByEditorId));
        if (kw.Miss is { } km)
            Drop(km == "is empty"
                ? "its keyword section is empty, so KID creates a keyword with no EditorID, logs 'FAIL - keyword editorID is empty!' and drops the line"
                : $"keyword '{kw.Raw}' {km}; KID logs 'FAIL - keyword doesn't exist' and drops the line");
        else if (kw.Created)
        {
            createdNote = notes.Count;
            notes.Add($"keyword '{kw.Raw}' is no keyword's EditorID{Spaced(kw.Raw, g)}, so KID creates a keyword of that name");
        }
        else if (kw.CreatedAs is { } ca)
        {
            createdNote = notes.Count;
            notes.Add($"keyword '{kw.Raw}' is no keyword's EditorID, but another KID line creates it as '{ca}' and KID finds a created keyword in any case, so KID creates no second one");
        }

        // The plain list: KID drops the line when every exact term fails, or every '-' term fails; one that fails while
        // another of its kind stands is skipped. A '+' group needs every part; the line drops when every group failed.
        var strings = new List<string>();
        var partial = new List<string>();
        var partialPaths = new List<string>();
        var models = new List<string>();
        var plain = new List<(Token T, string? Why)>();
        var groups = new SortedDictionary<int, List<(Token T, string? Why)>>();
        for (int i = 0; i < line.Tokens.Count; i++)
        {
            var t = line.Tokens[i];
            if (t.Field != KidField.Filters) continue;
            string? why = null;
            if (t.Partial) (IsModel(t.Raw, g) ? partialPaths : partial).Add(t.Written(g));
            else if (IsModel(t.Raw, g)) models.Add(t.Written(g));
            else
            {
                var r = resolved[i]!;
                var where = Where(t, g);
                if (r.Note is { } index) notes.Add($"{where} '{t.Raw}' {index}");
                if (r.Form is { } fk)
                {
                    var cls = ctx.ClassOf(fk);
                    bool target = !t.Not && SpidGrammar.IsOneOf(cls, new[] { type.Record });
                    forms.Add(new Named(target ? SkyPatcherOverlay.FormPlace.Target : SkyPatcherOverlay.FormPlace.Filter, where, t.Raw, fk, r.ByEditorId));
                    if (!SpidGrammar.IsOneOf(cls, g.FilterTypes))
                        why = $"names {FormIdToken.Of(fk)}, {RecordName(cls)} record, which KID does not take as a filter";
                }
                else if (r.Plugin is { } p)
                {
                    if (r.PluginPresent) plugins.Add(p); else why = r.Miss;
                }
                else if (r.ByEditorId)
                {
                    if (created.Contains(t.Raw)) notes.Add($"{where} '{t.Raw}' is no record's EditorID but names the keyword a KID line creates, so it matches the items that keyword is given to");
                    else strings.Add(t.Written(g));
                }
                else why = r.Miss;
            }
            var slot = (t, why);
            if (t.Group >= 0) { if (!groups.TryGetValue(t.Group, out var gl)) groups[t.Group] = gl = new(); gl.Add(slot); }
            else plain.Add(slot);
        }

        // An exact plain term counts toward KID's match list, a '-' one (a '-*' too, which always stands) toward its
        // exclusion list; a '*' term counts toward neither.
        foreach (var (not, word, widen) in new[] { (false, $"plain filter of the line (no '{g.Not}', '{g.Partial}' or '{g.All}')", "reaches more items"),
                                                   (true, $"'{g.Not}' filter of the line", "excludes fewer items") })
        {
            var list = plain.Where(x => x.T.Not == not && (not || !x.T.Partial)).ToList();
            var failed = list.Where(x => x.Why is not null).Select(x => $"{Where(x.T, g)} '{x.T.Raw}' {x.Why}").ToList();
            if (failed.Count == 0) continue;
            if (failed.Count == list.Count)
                Drop(string.Join("; ", failed) + $"; every {word} names nothing KID can use, so KID drops the line");
            else notes.AddRange(failed.Select(f => f + $"; KID skips it, so the line {widen}"));
        }
        if (groups.Count > 0)
        {
            var lost = groups.Where(kv => kv.Value.Any(x => x.Why is not null))
                .Select(kv => (Text: string.Join(g.All, kv.Value.Select(x => x.T.Written(g))),
                               First: kv.Value.First(x => x.Why is not null))).ToList();
            var said = lost.Select(x => $"'{g.All}' group '{x.Text}': '{x.First.T.Raw}' {x.First.Why}").ToList();
            if (lost.Count == groups.Count)
                Drop(string.Join("; ", said) + $"; a '{g.All}' group needs every part, and every group of the line lost one, so KID drops the line");
            else notes.AddRange(said.Select(s => s + $"; a '{g.All}' group needs every part, so KID drops that group and the line's other groups stand"));
        }

        if (strings.Count > 0)
            notes.Add($"filters {Quoted(strings)} {(strings.Count == 1 ? "is" : "are")} no record's EditorID and no keyword a KID line creates, so KID keeps " +
                      $"{(strings.Count == 1 ? "it" : "them")} as text and in the running game matches {(strings.Count == 1 ? "it" : "each")} against an item's EditorID or name " +
                      "(an actor-value or archetype name against the item's own), which is not read here");
        if (models.Count > 0)
            notes.Add($"filters {Quoted(models)} {(models.Count == 1 ? "ends" : "end")} in '{g.ModelEnding}', so KID matches {(models.Count == 1 ? "it" : "each")} " +
                      "against an item's whole model path in the running game, which is not read here");
        if (partial.Count > 0)
            notes.Add($"filters {Quoted(partial)} {(partial.Count == 1 ? "matches" : "match")} part of an item's EditorID, name or one of its keywords' EditorIDs " +
                      "in the running game, which is not read here");
        if (partialPaths.Count > 0)
            notes.Add($"filters {Quoted(partialPaths)} {(partialPaths.Count == 1 ? "ends" : "end")} in '{g.ModelEnding}', so KID matches {(partialPaths.Count == 1 ? "it" : "each")} " +
                      "against part of an item's model path in the running game, which is not read here");
        // The keyword is created before any filter is read, so a line its filters drop still creates it.
        if (createdNote >= 0) notes[createdNote] += dropped is null ? " and hands it out" : ", which the dropped line hands to no item";
        return new LineRead(forms, plugins, notes, dropped);
    }

    static LineRead ReadGroup(Line line, IReadOnlyList<Resolved?> resolved, HashSet<string> created, Context ctx)
    {
        var g = ctx.G;
        var forms = new List<Named>();
        var notes = new List<string>();
        int kept = 0;
        for (int i = 0; i < line.Tokens.Count; i++)
        {
            var t = line.Tokens[i];
            var r = resolved[i]!;
            var where = Where(t, g);
            if (r.Note is { } index) notes.Add($"{where} '{t.Raw}' {index}");
            string? why;
            if (r.Form is { } fk)
            {
                forms.Add(new Named(SkyPatcherOverlay.FormPlace.Filter, where, t.Raw, fk, r.ByEditorId));
                var cls = ctx.ClassOf(fk);
                why = SpidGrammar.IsOneOf(cls, new[] { g.KeywordRecord }) ? null : $"names {FormIdToken.Of(fk)}, {RecordName(cls)} record, not a keyword";
            }
            else if (r.Plugin is not null) why = "names a plugin, not a keyword";
            else if (r.ByEditorId && created.Contains(t.Raw)) why = null;
            else why = (r.Miss ?? "names nothing") + Spaced(t.Raw, g);
            if (why is null) { if (!t.Not) kept++; continue; }
            notes.Add($"{where} '{t.Raw}' {why}; KID logs it and leaves it out of the group");
        }
        return new LineRead(forms, Array.Empty<string>(), notes, kept == 0 ? $"group '{line.Keyword}' keeps no keyword, so KID drops it" : null);
    }

    /// <summary>Where a token sits, as a line note and a label spell it: its section, and its '-' or '+' list.</summary>
    static string Where(Token t, KidGrammar g)
        => (t.Field == KidField.Members ? "members" : "filters") + (t.Not ? " " + g.Not : t.Group >= 0 ? " " + g.All : "");

    /// <summary>The words a name owes when it carries a space KID keeps.</summary>
    static string Spaced(string raw, KidGrammar g)
        => raw.Trim() != raw ? $" (KID keeps the space: it does not trim around '{g.Sections}' or '{g.Item}' here)" : "";

    static string Quoted(IEnumerable<string> xs) => string.Join(", ", xs.Select(x => "'" + x + "'"));

    /// <summary>A record class as a line names it.</summary>
    static string RecordName(Type? cls)
    {
        var name = cls is null ? "unknown" : RecordNaming.StripOverlay(cls.Name);
        return ("AEIOU".Contains(char.ToUpperInvariant(name[0])) ? "an " : "a ") + name;
    }
}

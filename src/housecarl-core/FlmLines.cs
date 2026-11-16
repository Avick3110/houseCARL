using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Named = HousecarlCore.DistributorLines.Named;
using Resolved = HousecarlCore.DistributorLines.Resolved;
using Place = HousecarlCore.SkyPatcherOverlay.FormPlace;

namespace HousecarlCore;

/// <summary>FLM's <c>_FLM.ini</c> lines read as FLM 1.8.1 reads them (rules and sources: docs/architecture/flm-layer.md):
/// the unnamed section, each value sanitized as FLM sanitizes it and split on '|' and ','; Filter and Collection lines of
/// every file first, then file by file the Alias and Group lines and the rest, each file key by key; every form a line
/// names resolved through the one target resolver under FLM's row and labelled target (a list), value (what is added)
/// or filter (a collection's keyword), through the group, alias, list or collection that holds it; each line's filter
/// evaluated against the order's plugins; and what each kept line adds to which list, a form already there skipped.
/// Collection members, mod events and what a list holds in the running game are not read.</summary>
public static class FlmLines
{
    /// <summary>One line of a file as FLM splits it: the file it is in, its key as written and as FLM knows it, its
    /// sanitized '|' fields, why FLM reads no entry from it (null when it reads one) and the notes the split owes.</summary>
    public sealed record Line(int Number, string Raw, string File, string Key, FlmKey? Spec, IReadOnlyList<string> Fields,
                              string? Skipped, IReadOnlyList<string> Notes) : DistributorLines.ILine
    {
        /// <summary>The header's count key: the key as FLM's table spells it.</summary>
        public string HeaderKey => Spec?.Key ?? Key;
    }

    // ---- splitting a file ---------------------------------------------------------------

    /// <summary>Every key line of one file's text in file order, read or skipped, as SimpleIni and FLM meet it, under
    /// <paramref name="format"/>'s line grammar. <paramref name="file"/> is the file's Data-relative path, which scopes
    /// the file's aliases and groups.</summary>
    public static IReadOnlyList<Line> Parse(string text, DistributorFormat format, string file)
    {
        var g = format.FlmLines!;
        var lines = new List<Line>();
        var physical = text.Split('\n');
        string? section = null;   // null: the unnamed section, the only one FLM reads
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
            if (eq < 0) { lines.Add(Skip(i + 1, raw, file, t, $"has no '{g.Assign}', and FLM's SimpleIni takes no key without one, so FLM reads no entry from it")); continue; }
            var key = t[..eq].Trim();
            if (key.Length == 0) { lines.Add(Skip(i + 1, raw, file, key, $"has no key before '{g.Assign}', so FLM reads no entry from it")); continue; }
            if (section is not null)
            {
                lines.Add(Skip(i + 1, raw, file, key, $"sits under [{section}], and FLM reads only the unnamed section: the lines before the first named header and any after an empty '[]'"));
                continue;
            }
            if (g.Key(key) is not { } spec)
            {
                lines.Add(Skip(i + 1, raw, file, key, $"has key '{key}', which is not one of FLM's {g.Keys.Count} keys ({string.Join(", ", g.Keys.Select(k => k.Key))}, in any case), so FLM logs 'Unknown key {key}!' and counts it invalid"));
                continue;
            }
            var value = t[(eq + 1)..].Trim();
            var notes = new List<string>();
            var clean = Sanitize(value, notes);
            if (spec.Kind == FlmKind.Filter) clean = SanitizeFilter(clean);
            lines.Add(new Line(i + 1, raw, file, key, spec, Split(clean, g.Fields), null, notes));
        }
        return lines;
    }

    static Line Skip(int n, string raw, string file, string key, string why) => new(n, raw, file, key, null, Array.Empty<string>(), why, Array.Empty<string>());

    /// <summary>CLibUtil's split: every field between the delimiters, an empty one kept, and none at all for empty text.</summary>
    public static IReadOnlyList<string> Split(string s, char c) => s.Length == 0 ? Array.Empty<string>() : s.Split(c);

    const string Space = "[ \\t\\n\\v\\f\\r]*";
    static readonly Regex Bar = new(Space + "\\|" + Space, RegexOptions.CultureInvariant),
        Comma = new(Space + "," + Space, RegexOptions.CultureInvariant),
        Zeros = new("(0x00+)([0-9a-fA-F]+)", RegexOptions.CultureInvariant),
        Dawnguard = new("(0x0*2)([0-9a-f]{6})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        Dragonborn = new("(0x0*4)([0-9a-f]{6})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>FLM's Sanitize (Utility.hpp:98-123): spaces round '|' and ',' stripped, leading zeros after '0x' dropped,
    /// then a FormID written with index 02 or 04 rewritten to Dawnguard's or Dragonborn's, in any case and anywhere in
    /// the value; each rewrite is noted, as a load index written in is (DECISIONS 2026-11-13 (12)(ii)).</summary>
    public static string Sanitize(string value, List<string>? notes = null)
    {
        var s = Comma.Replace(Bar.Replace(value, "|"), ",");
        s = Zeros.Replace(s, "0x$2");
        if (!s.Contains("0x", StringComparison.OrdinalIgnoreCase)) return s;
        foreach (var (re, plugin, index) in Dlc)
            s = re.Replace(s, m =>
            {
                var to = "0x" + m.Groups[2].Value + "~" + plugin;
                notes?.Add($"FLM rewrites '{m.Value}' to '{to}' before it reads the line (its Sanitize reads a FormID written with load index " +
                           $"{index} as {plugin}'s, whatever plugin is written after it, which it then ignores)");
                return to;
            });
        return s;
    }

    static readonly (Regex Re, string Plugin, string Index)[] Dlc = { (Dawnguard, "Dawnguard.esm", "02"), (Dragonborn, "Dragonborn.esm", "04") };

    /// <summary>FLM's SanitizeFilter (Utility.hpp:140-149): spaces round ',' stripped and every letter lowercased.</summary>
    public static string SanitizeFilter(string value) => Lower(Comma.Replace(value, ","));

    /// <summary>std::tolower in the C locale: ASCII letters only.</summary>
    static string Lower(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (int i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
    });

    // ---- the filter ---------------------------------------------------------------------

    /// <summary>A filter's verdict as FLM's EvaluateFilter gives it: true (1), false (-1) or invalid (0), and why.</summary>
    public sealed record Verdict(int Value, string Why, IReadOnlyList<string> Notes);

    /// <summary>FLM's SplitFilterConditions (Utility.hpp:231-260, 1.8.1 the same): from the last cut, the nearest
    /// plugin extension ends a condition, and the split goes on only while more than one extension kind is still
    /// ahead; <paramref name="end"/> is where the last condition read ends (-1 for none).</summary>
    public static IReadOnlyList<string> SplitConditions(string f, IReadOnlyList<string> extensions, out int end)
    {
        var conditions = new List<string>();
        int next = -1, found;
        do
        {
            int prev = next + 1;
            int min = int.MaxValue;
            found = 0;
            foreach (var e in extensions)
                if (prev <= f.Length && f.IndexOf(e, prev, StringComparison.Ordinal) is var p and > 0) { found++; min = Math.Min(min, p); }
            if (found == 0) break;
            next = min + 4;
            conditions.Add(f.Substring(prev, next - prev));
        } while (found > 1);
        end = next;
        return conditions;
    }

    /// <summary>FLM's EvaluateFilter (Utility.hpp:267-303) on sanitized conditions: ',' separates alternatives, the first
    /// whose every condition holds makes the filter true; a condition not led by '+' or '-' makes it invalid.</summary>
    public static Verdict Evaluate(string filter, FlmGrammar g, Func<string, bool> present)
    {
        var notes = new List<string>();
        var failed = new List<string>();
        foreach (var cs in Split(filter, ','))
        {
            var conditions = SplitConditions(cs, g.FilterExtensions, out int end);
            if (end >= 0 && end < cs.Length)
                notes.Add($"FLM checks '{string.Join("", conditions)}' of '{cs}' and never '{cs[end..]}': its split reads on only while another of {string.Join(", ", g.FilterExtensions)} is still ahead");
            if (conditions.Count == 0) return new Verdict(1, $"'{cs}' names no plugin extension, which FLM reads as true", notes);
            string? fails = null;
            foreach (var c in conditions)
            {
                if (c[0] != g.Require && c[0] != g.Not) return new Verdict(0, $"'{c}' does not start with '{g.Require}' or '{g.Not}'", notes);
                var plugin = c[1..];
                bool on = present(plugin);
                if (on != (c[0] == g.Require)) { fails = $"'{c}' ({plugin} is {(on ? "" : "not ")}in the order)"; break; }
            }
            if (fails is null) return new Verdict(1, $"'{cs}' holds", notes);
            failed.Add(fails);
        }
        return new Verdict(-1, failed.Count == 0 ? "it names no condition" : string.Join(", ", failed) + (failed.Count == 1 ? " fails" : " fail"), notes);
    }

    // ---- what a line names --------------------------------------------------------------

    /// <summary>What one line names and what FLM does with it: every form it names that resolves, the notes its tokens
    /// owe, why FLM counts it invalid (null when FLM keeps it), whether its filter lets it apply, and what it adds.</summary>
    public sealed record LineRead(IReadOnlyList<Named> Forms, IReadOnlyList<string> Notes, string? Dropped, bool FilteredOut,
                                  IReadOnlyList<Add> Adds) : DistributorLines.IRead
    {
        /// <summary>FLM names no plugin as a term: a filter's plugins gate the line (DECISIONS 2026-11-13 (12)(iii)).</summary>
        public IReadOnlyList<string> PluginTerms => Array.Empty<string>();
    }

    /// <summary>What a kept line adds to one list: the list, the forms it adds in order (each with the word the answer
    /// names it by), the collections whose members it adds too (not read), and, for a pair, the other half's list and form.</summary>
    public sealed record Add(FormKey List, string ListName, IReadOnlyList<(FormKey Form, string Name)> Forms, IReadOnlyList<string> Collections,
                             (FormKey List, string ListName, FormKey Form, string Name)? Pair = null);

    /// <summary>The lookups a read needs: the call's target resolver, the class of a resolved record off the order, the
    /// forms a list's winning record holds, and the format's row.</summary>
    public sealed record Context(SkyPatcherOverlay.IFormResolver Resolver, Func<FormKey, Type?> ClassOf,
                                 Func<FormKey, IReadOnlyList<FormKey>?> ItemsOf, DistributorFormat Format)
    {
        internal FlmGrammar G => Format.FlmLines!;
        internal TargetSpelling S => Format.Targets;
    }

    /// <summary>One token as FLM takes it: the resolve, and the note a ten-character FormID side owes.</summary>
    sealed record Taken(Resolved R, string? Trim);

    /// <summary>The order FLM visits one file's lines in: key by key (A to Z, any case, SimpleIni's multimap), each key's lines in file order.</summary>
    public static IEnumerable<int> KeyOrder(IReadOnlyList<Line> lines, IEnumerable<int> of)
        => of.OrderBy(i => Lower(lines[i].Key), StringComparer.Ordinal).ThenBy(i => lines[i].Number);

    /// <summary>The flat indices of each file's lines, files in the order given, a file's lines contiguous.</summary>
    static List<List<int>> Files(IReadOnlyList<Line> lines)
    {
        var files = new List<List<int>>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == 0 || lines[i].File != lines[i - 1].File) files.Add(new List<int>());
            files[^1].Add(i);
        }
        return files;
    }

    /// <summary>FLM's two passes over the files (Manipulator.hpp:391-533): pass 1 every file's Filter lines, then its
    /// Collection lines; pass 2, file by file, its Alias and Group lines, then the rest.</summary>
    public static IEnumerable<int> Visits(IReadOnlyList<Line> lines)
    {
        var files = Files(lines);
        bool Is(int i, params FlmKind[] k) => lines[i].Spec is { } s && k.Contains(s.Kind);
        foreach (var f in files)
        {
            foreach (var i in KeyOrder(lines, f.Where(i => Is(i, FlmKind.Filter)))) yield return i;
            foreach (var i in KeyOrder(lines, f.Where(i => Is(i, FlmKind.Collection)))) yield return i;
        }
        foreach (var f in files)
        {
            foreach (var i in KeyOrder(lines, f.Where(i => Is(i, FlmKind.Alias, FlmKind.Group)))) yield return i;
            foreach (var i in KeyOrder(lines, f.Where(i => lines[i].Spec is { Kind: not (FlmKind.Filter or FlmKind.Collection or FlmKind.Alias or FlmKind.Group) }))) yield return i;
        }
    }

    /// <summary>The names FLM holds as it reads: filters (lowercased), collections, aliases and groups, each first-defined wins.</summary>
    sealed class Names
    {
        public readonly Dictionary<string, (bool On, string At)> Filters = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string At, IReadOnlyList<Named> Keywords, bool Registered)> Collections = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string At, IReadOnlyList<(FormKey List, string Name)> Lists)> Aliases = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string At, IReadOnlyList<Named> Members, IReadOnlyList<string> Collections)> Groups = new(StringComparer.Ordinal);
    }

    /// <summary>Read every line of <paramref name="lines"/> (every file's, in FLM's file order) as FLM does, in FLM's
    /// passes: each distinct token resolved once (an every-type lookup, as FLM's untyped one is). A skipped line reads as null.</summary>
    public static IReadOnlyList<LineRead?> ReadAll(IReadOnlyList<Line> lines, Context ctx)
    {
        // A token's read depends only on its text in this call, so each distinct one is resolved once; the map dies with the call.
        var tokens = new Dictionary<string, Taken>(StringComparer.Ordinal);
        Taken Of(string raw) => tokens.TryGetValue(raw, out var t) ? t : tokens[raw] = Take(raw, ctx);
        var names = new Names();
        var reads = new LineRead?[lines.Count];
        foreach (var i in Visits(lines)) reads[i] = new Reader(lines[i], Of, ctx, names).Read();
        return reads;
    }

    static Taken Take(string raw, Context ctx)
    {
        if (raw.Length == 0) return new Taken(new Resolved(null, null, false, "is empty, so it names nothing", null, false), null);
        // FLM reads hex with no '~' as a FormID only where '0x' is written in lower case.
        var s = raw.Contains(ctx.S.Separator) || raw.Contains("0x", StringComparison.Ordinal) ? ctx.S : ctx.S with { BareFormId = TargetBareFormId.NotAFormId };
        var r = DistributorLines.Resolve(raw, ctx.Resolver, s, "FLM");
        int at = raw.IndexOf(ctx.S.Separator);
        string? trim = at == RuntimeFormId.Digits + 2 && RuntimeTargets.ParseHex(raw[..at]) is not null
            ? $"has the ten-character FormID '{raw[..at]}', from which FLM erases '{raw.Substring(2, 2)}' before it adds the rest to its plugin's own index"
            : null;
        return new Taken(r, trim);
    }

    /// <summary>One line read in FLM's order, against the names FLM holds by then.</summary>
    sealed class Reader(Line line, Func<string, Taken> of, Context ctx, Names names)
    {
        readonly FlmGrammar g = ctx.G;
        readonly List<Named> forms = new();
        readonly List<string> notes = new();
        readonly List<Add> adds = new();
        string? dropped;
        bool filteredOut;
        int? stopAt;   // the notes a false or invalid filter leaves, for a key whose forms FLM reads only past it
        IReadOnlyList<string> F => line.Fields;
        string At => line.File + ":" + line.Number;

        void Drop(string why) => dropped ??= why;

        public LineRead Read()
        {
            var spec = line.Spec!;
            switch (spec.Kind)
            {
                case FlmKind.Filter: Filter(); break;
                case FlmKind.Collection: Collection(); break;
                case FlmKind.Alias: Alias(); break;
                case FlmKind.Group: Group(); break;
                case FlmKind.FormList: FormList(); break;
                case FlmKind.Pair: Pair(spec); break;
                case FlmKind.List: List(spec); break;
                case FlmKind.ModEvent:
                    notes.Add($"a {spec.Key} line acts only when a mod sends its event in the running game, which is not read here");
                    break;
                case FlmKind.Remove:
                    notes.Add($"FLM 1.8.1 counts a {spec.Key} line valid and does nothing with it at data load: its parser stores nothing");
                    break;
                case FlmKind.FormsPair:
                    Drop($"FLM 1.8.1's {spec.Key} parser returns false for every line, so FLM counts it invalid and reads nothing from it");
                    break;
            }
            // FLM reads such a line's forms only once its filter passes, so it logs nothing for them; their labels stay.
            if (stopAt is int n && spec.Kind is FlmKind.FormList or FlmKind.Pair or FlmKind.List)
            {
                notes.RemoveRange(n, notes.Count - n);
                if (filteredOut) dropped = null;
            }
            return new LineRead(forms, notes, dropped, filteredOut && dropped is null, dropped is null && !filteredOut ? adds : Array.Empty<Add>());
        }

        bool Sections(string shape, params int[] counts)
        {
            if (counts.Contains(F.Count)) return true;
            Drop($"has {F.Count} '{g.Fields}' field(s), and FLM's {line.Spec!.Key} takes {string.Join(" or ", counts)} ({shape}), so FLM logs 'Wrong {line.Spec.Key} format' and counts it invalid");
            return false;
        }

        // ---- tokens ----

        /// <summary>Why a token names nothing, as a clause the line's own words follow.</summary>
        static string? Miss(Taken t) => t.R.Miss?.TrimEnd('.');

        FormKey? Form(string where, string token, string what)
        {
            var t = of(token);
            if (t.R.Note is { } index) notes.Add($"{where} '{token}' {index}");
            if (t.Trim is { } trim) notes.Add($"{where} '{token}' {trim}");
            if (t.R.Form is not { } fk) return null;
            if (what.Length > 0 && !SpidGrammar.IsOneOf(ctx.ClassOf(fk), new[] { what }))
                return null;
            return fk;
        }

        /// <summary>Why a token found no record of <paramref name="type"/> (empty for any type).</summary>
        string Why(string token, string type)
        {
            var t = of(token);
            if (t.R.Form is { } fk && type.Length > 0 && ctx.ClassOf(fk) is var cls && !SpidGrammar.IsOneOf(cls, new[] { type }))
                return $"names {FormIdToken.Of(fk)}, a record of type {(cls is null ? "unknown" : RecordNaming.StripOverlay(cls.Name))}, not a {type}";
            return Miss(t) ?? "names nothing";
        }

        // ---- the filter field ----

        /// <summary>FLM's evaluateFilter on a line's filter field: '#' a named filter, else the conditions inline. True
        /// lets the line read on; false or invalid stops it (FLM reads the filter before any form).</summary>
        bool Gate(string filter)
        {
            Verdict v;
            if (filter.Length > 0 && filter[0] == g.Name)
            {
                var name = Lower(filter[1..]);
                v = names.Filters.TryGetValue(name, out var f)
                    ? new Verdict(f.On ? 1 : -1, $"the Filter line at {f.At} is {(f.On ? "true" : "false")}", Array.Empty<string>())
                    : new Verdict(0, $"no Filter line defines '{name}'", Array.Empty<string>());
            }
            else v = Evaluate(SanitizeFilter(filter), g, ctx.Resolver.PluginPresent);
            notes.AddRange(v.Notes);
            switch (v.Value)
            {
                case 1: notes.Add($"its filter '{filter}' is true: {v.Why}"); return true;
                case -1:
                    filteredOut = true;
                    notes.Add($"its filter '{filter}' is false: {v.Why}, so FLM counts the line filtered out and adds nothing from it");
                    stopAt = notes.Count;
                    return false;
                default:
                    Drop($"its filter '{filter}' is invalid ({v.Why}), so FLM logs 'Filter … was omitted because it is invalid' and counts the line invalid");
                    stopAt = notes.Count;
                    return false;
            }
        }

        // ---- the keys ----

        void Filter()
        {
            if (!Sections("name|conditions", 2)) return;
            var name = F[0];
            if (names.Filters.TryGetValue(name, out var first))
            {
                Drop($"names filter '{name}', which {first.At} defines first (FLM lowercases filter names), so FLM logs 'Filter {name} exists.' and omits this one");
                return;
            }
            var v = Evaluate(F[1], g, ctx.Resolver.PluginPresent);
            notes.AddRange(v.Notes);
            if (v.Value == 0) { Drop($"its conditions are invalid ({v.Why}), so FLM logs 'Filter \"{name}\" was omitted because it is invalid'"); return; }
            names.Filters[name] = (v.Value == 1, At);
            notes.Add($"filter '{name}' is {(v.Value == 1 ? "true" : "false")} on this order: {v.Why}");
        }

        void Collection()
        {
            if (!Sections("name|type|keywords|filter", 3, 4)) return;
            var name = F[0];
            if (names.Collections.TryGetValue(name, out var first) && first.Registered)
            {
                Drop($"names collection '{name}', which {first.At} defines first, so FLM logs 'Collection {name} exists.' and omits this one");
                return;
            }
            var type = Lower(F[1]);
            if (!g.CollectionTypes.Contains(type))
            {
                Drop($"has type '{F[1]}', which is not one of FLM's collection types ({string.Join(", ", g.CollectionTypes)}, in any case), so FLM counts it invalid");
                return;
            }
            var keywords = new List<Named>();
            foreach (var raw in Split(F[2], g.Item))
            {
                bool not = raw.Length > 0 && raw[0] == g.Not;
                var k = not ? raw[1..] : raw;
                var where = not ? "keywords " + g.Not : "keywords";
                if (Form(where, k, g.KeywordRecord) is not { } fk)
                {
                    Drop($"keyword '{k}' {Why(k, g.KeywordRecord)} (a keyword another framework makes in the running game is not read), so FLM logs 'Keyword {k} do not exist!' and drops the whole collection");
                    return;
                }
                keywords.Add(new Named(Place.Filter, where, k, fk, of(k).R.ByEditorId));
            }
            forms.AddRange(keywords);
            bool registered = F.Count < 4 || Gate(F[3]);
            if (!registered && dropped is null)
                notes.Add($"FLM has stored its keywords before it reads the filter, so collection '{name}' may still be filled at load (members are not read here)");
            names.Collections[name] = (At, keywords, registered);
            if (registered) notes.Add($"its members (every {type} with the keywords listed and none of the '{g.Not}' ones) are not read here");
        }

        void Alias()
        {
            if (!Sections("name|lists", 2)) return;
            var name = F[0];
            var lists = new List<(FormKey, string)>();
            foreach (var l in Split(F[1], g.Item))
            {
                if (Form("alias list", l, g.ListRecord) is { } fk)
                {
                    forms.Add(new Named(Place.Target, "alias lists", l, fk, of(l).R.ByEditorId));
                    lists.Add((fk, l));
                }
                else notes.Add($"alias list '{l}' {Why(l, g.ListRecord)}; FLM logs 'Unable to find FormList: {l} for Alias.' and leaves it out");
            }
            if (names.Aliases.TryGetValue(name, out var first))
                Drop($"names alias '{name}', which {first.At} defines first, so FLM logs 'Alias {name} exists.' and omits this one");
            else if (lists.Count == 0)
                Drop($"names no list FLM finds, so FLM omits alias '{name}' ('does not have valid Forms')");
            else names.Aliases[name] = (At, lists);
        }

        void Group()
        {
            if (!Sections("name|forms", 2)) return;
            var name = F[0];
            var members = new List<Named>();
            var colls = new List<string>();
            foreach (var m in Split(F[1], g.Item))
            {
                if (m.Length > 0 && m[0] == g.ListForms) members.AddRange(ListForms(m, "group members"));
                else if (m.Length > 0 && m[0] == g.Name)
                {
                    var c = m[1..];
                    if (names.Collections.TryGetValue(c, out var coll))
                    {
                        Through(coll.Keywords, "group members through collection", m);
                        colls.Add(c);
                    }
                    else notes.Add($"group member '{m}' names no collection FLM holds, so FLM logs 'Unknown Collection: {c}.' and skips it");
                }
                else if (Form("group member", m, "") is { } fk) members.Add(new Named(Place.Value, "group members", m, fk, of(m).R.ByEditorId));
                else notes.Add($"group member '{m}' {Why(m, "")}; FLM logs 'Unable to find Form: {m} for Group.' and leaves it out");
            }
            forms.AddRange(members);
            if (names.Groups.TryGetValue(name, out var first))
                Drop($"names group '{name}', which {first.At} defines first, so FLM logs 'Group {name} exists.' and omits this one");
            else if (members.Count == 0 && colls.Count == 0)
                Drop($"holds no form FLM finds, so FLM omits group '{name}' ('does not have valid Forms')");
            else
            {
                if (members.Count == 0)
                    notes.Add($"FLM keeps group '{name}' only if {Prose(colls.Select(c => "collection '" + c + "'").ToList())} holds a form at load, which is not read here");
                names.Groups[name] = (At, members, colls);
            }
        }

        /// <summary>A '*list' item: the list itself, labelled, and each form its winning record holds, through it (FLM reads
        /// the list's own forms at parse, before any FLM add; Manipulator.hpp:917, :1470).</summary>
        List<Named> ListForms(string token, string where)
        {
            var name = token[1..];
            var got = new List<Named>();
            if (Form(where + " list", name, g.ListRecord) is not { } list)
            {
                notes.Add($"{where[..^1]} '{token}' {Why(name, g.ListRecord)}; FLM logs 'Unable to find FormList: {name}.' and skips it");
                return got;
            }
            forms.Add(new Named(Place.Value, where + ": list", name, list, of(name).R.ByEditorId));
            if (ctx.ItemsOf(list) is not { } items)
            {
                notes.Add($"the forms of list '{name}' ({FormIdToken.Of(list)}) could not be read");
                return got;
            }
            got.AddRange(items.Select(i => new Named(Place.Value, where + " through list", token, i, false)));
            return got;
        }

        void Through(IReadOnlyList<Named> keywords, string where, string token)
            => forms.AddRange(keywords.Select(k => new Named(Place.Filter, where, token, k.Form, false)));

        /// <summary>A line's items (FLM's parseFormEntry, Manipulator.hpp:1434-1486): '#' a group's forms and a collection's
        /// (members not read), '*' a list's forms, else any form; one naming nothing is logged and the rest are added.</summary>
        (List<(FormKey, string)> Forms, List<string> Collections) Items(string field)
        {
            var got = new List<(FormKey, string)>();
            var colls = new List<string>();
            foreach (var item in Split(field, g.Item))
            {
                if (item.Length > 0 && item[0] == g.Name)
                {
                    var n = item[1..];
                    bool known = false;
                    if (names.Groups.TryGetValue(n, out var grp))
                    {
                        known = true;
                        foreach (var m in grp.Members)
                        {
                            forms.Add(new Named(m.Place, "items through group", item, m.Form, false));
                            got.Add((m.Form, FormIdToken.Of(m.Form)));
                        }
                        foreach (var c in grp.Collections)
                            if (names.Collections.TryGetValue(c, out var gc)) { Through(gc.Keywords, "items through group's collection", item); colls.Add(c); }
                    }
                    if (names.Collections.TryGetValue(n, out var coll))
                    {
                        known = true;
                        Through(coll.Keywords, "items through collection", item);
                        colls.Add(n);
                    }
                    if (!known) notes.Add($"item '{item}' names no group or collection FLM holds by then (a group counts from its own file on), so FLM logs 'Unknown Group/Collection: {n}.' and skips it");
                }
                else if (item.Length > 0 && item[0] == g.ListForms)
                    foreach (var m in ListForms(item, "items"))
                    {
                        forms.Add(m);
                        got.Add((m.Form, FormIdToken.Of(m.Form)));
                    }
                else if (Form("item", item, "") is { } fk)
                {
                    forms.Add(new Named(Place.Value, "items", item, fk, of(item).R.ByEditorId));
                    got.Add((fk, item));
                }
                else notes.Add(item.Length == 0
                    ? "an empty item (a ',' with nothing after it) names nothing; FLM logs 'Unable to find Form: .' and adds the rest"
                    : $"item '{item}' {Why(item, "")}; FLM logs 'Unable to find Form: {item}.' and adds the rest");
            }
            return (got, colls);
        }

        /// <summary>One of a simplified key's fixed lists, by its EditorID, marked implied (DECISIONS 2026-11-13 (14) Q3).</summary>
        FormKey? Implied(string eid)
        {
            if (Form("implied list", eid, g.ListRecord) is { } fk)
            {
                forms.Add(new Named(Place.Target, "implied list", eid, fk, true));
                return fk;
            }
            notes.Add($"its implied list '{eid}' {Why(eid, g.ListRecord)}; FLM logs 'unable to find list: {eid}.'");
            return null;
        }

        void FormList()
        {
            if (!Sections("list|forms|filter", 2, 3)) return;
            if (F.Count == 3) Gate(F[2]);
            var lists = new List<(FormKey, string)>();
            var target = F[0];
            if (target.Length > 0 && target[0] == g.Name)
            {
                var a = target[1..];
                if (names.Aliases.TryGetValue(a, out var alias))
                    foreach (var (fk, n) in alias.Lists)
                    {
                        forms.Add(new Named(Place.Target, "list through alias", target, fk, false));
                        lists.Add((fk, n));
                    }
                else Drop($"its list '{target}' names alias '{a}', which no Alias line defines in this file or an earlier one, so FLM logs 'Unknown Alias: {a}.' and counts the line invalid");
            }
            else if (Form("list", target, g.ListRecord) is { } fk)
            {
                forms.Add(new Named(Place.Target, "list", target, fk, of(target).R.ByEditorId));
                lists.Add((fk, target));
            }
            else Drop($"its list '{target}' {Why(target, g.ListRecord)}, so FLM logs 'Unable to find FormList: {target}.' and counts the line invalid");
            var (items, colls) = Items(F[1]);
            foreach (var (fk, n) in lists) adds.Add(new Add(fk, n, items, colls));
        }

        void Pair(FlmKey spec)
        {
            if (!Sections(spec.Names[0] + "|" + spec.Names[1] + "|filter", 2, 3)) return;
            if (F.Count == 3) Gate(F[2]);
            var halves = new FormKey?[2];
            for (int h = 0; h < 2; h++)
            {
                if (Form(spec.Names[h], F[h], "") is { } fk)
                {
                    forms.Add(new Named(Place.Value, spec.Names[h], F[h], fk, of(F[h]).R.ByEditorId));
                    halves[h] = fk;
                    var types = h == 0 ? g.SeedTypes : g.PlantTypes;
                    if (spec.Key == "Plant" && ctx.ClassOf(fk) is { } cls && !SpidGrammar.IsOneOf(cls, types))
                        notes.Add($"{spec.Names[h]} '{F[h]}' is a {RecordNaming.StripOverlay(cls.Name)}, not {Prose(types.ToList(), "or")}; FLM logs a warning and keeps it");
                }
                else Drop($"its {spec.Names[h]} '{F[h]}' {Why(F[h], "")}, so FLM logs 'Unable to find {Cap(spec.Names[h])}: {F[h]}.' and counts the line invalid");
            }
            var l0 = Implied(spec.Lists[0]);
            var l1 = Implied(spec.Lists[1]);
            if (!g.Adds(spec))
                notes.Add($"FLM 1.8.1 reads {spec.Key} lines and never adds them (its fill step has no {spec.Key} list), so this line adds nothing");
            else if (halves[0] is { } a && halves[1] is { } b && l0 is { } la && l1 is { } lb)
                adds.Add(new Add(la, spec.Lists[0], new[] { (a, F[0]) }, Array.Empty<string>(), (lb, spec.Lists[1], b, F[1])));
        }

        void List(FlmKey spec)
        {
            if (!Sections("forms|filter", 1, 2)) return;
            if (F.Count == 2) Gate(F[1]);
            var list = Implied(spec.Lists[0]);
            var (items, colls) = Items(F[0]);
            if (list is { } l && g.Adds(spec)) adds.Add(new Add(l, spec.Lists[0], items, colls));
        }

        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    static string Prose(IReadOnlyList<string> xs, string last = "and")
        => xs.Count <= 1 ? string.Concat(xs) : string.Join(", ", xs.Take(xs.Count - 1)) + " " + last + " " + xs[^1];

    // ---- what each line adds ------------------------------------------------------------

    /// <summary>Each kept line's add rows, by (file index, line index): FLM fills its lists in the row's add order
    /// (simplified keys first, FormList lines last; Manipulator.hpp:284-296), each key's lines in FLM's pass-2 order, and
    /// skips a form already in the list: its winning record's own, or one an earlier add put there (Utility.hpp:188-196).</summary>
    public static IReadOnlyDictionary<(int File, int Line), string> Rows(
        IReadOnlyList<(IReadOnlyList<Line> Lines, IReadOnlyList<LineRead?> Reads)> files, Context ctx)
    {
        var g = ctx.G;
        var flat = files.SelectMany(f => f.Lines).ToList();
        var place = new List<(int File, int Line)>();
        for (int fi = 0; fi < files.Count; fi++) for (int li = 0; li < files[fi].Lines.Count; li++) place.Add((fi, li));
        var reads = files.SelectMany(f => f.Reads).ToList();
        var order = Visits(flat).ToList();
        var have = new Dictionary<FormKey, Dictionary<FormKey, string>>();
        Dictionary<FormKey, string> In(FormKey list)
        {
            if (have.TryGetValue(list, out var h)) return h;
            h = new Dictionary<FormKey, string>();
            foreach (var f in ctx.ItemsOf(list) ?? []) h.TryAdd(f, "the list's own record");
            return have[list] = h;
        }
        var rows = new Dictionary<(int, int), StringBuilder>();
        var byKey = order.Where(i => flat[i].Spec is not null && reads[i] is not null).ToLookup(i => flat[i].Spec!.Key);
        foreach (var key in g.AddOrder)
            foreach (var i in byKey[key])
            {
                var r = reads[i]!;
                var (fi, li) = place[i];
                string here = flat[i].File + ":" + flat[i].Number;
                foreach (var add in r.Adds)
                {
                    if (!rows.TryGetValue((fi, li), out var sb)) rows[(fi, li)] = sb = new StringBuilder();
                    string From(string at) => at == here ? "earlier on this line" : at;
                    if (add.Pair is { } p)
                    {
                        var a = In(add.List);
                        var b = In(p.List);
                        var (form, name) = add.Forms[0];
                        if (a.TryGetValue(form, out var w1)) sb.Append($"          skips the pair: {name} is already in {add.ListName} ({From(w1)})\n");
                        else if (b.TryGetValue(p.Form, out var w2)) sb.Append($"          skips the pair: {p.Name} is already in {p.ListName} ({From(w2)})\n");
                        else
                        {
                            a[form] = here;
                            b[p.Form] = here;
                            sb.Append($"          adds {name} to {add.ListName} and {p.Name} to {p.ListName}\n");
                        }
                        continue;
                    }
                    var h = In(add.List);
                    int added = 0;
                    var skipped = new List<string>();
                    foreach (var (form, name) in add.Forms)
                    {
                        if (h.TryGetValue(form, out var w)) { skipped.Add($"{name} ({From(w)})"); continue; }
                        h[form] = here;
                        added++;
                    }
                    sb.Append($"          adds {added} form(s) to {add.ListName} ({FormIdToken.Of(add.List)})");
                    if (skipped.Count > 0) sb.Append($", skipping {skipped.Count} already in it: {string.Join(", ", skipped)}");
                    if (add.Collections.Count > 0) sb.Append($", and what {Prose(add.Collections.Distinct().Select(c => "collection '" + c + "'").ToList())} holds (not read)");
                    sb.Append('\n');
                }
            }
        return rows.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }
}

using System.Text;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Prints the skill paragraphs that state SkyPatcher's and SPID's discovery, order, conflict and grammar rules from the runtime-format source, into marked regions of the committed skill files; RuntimeFormatSourceTests holds the committed text to a fresh print.</summary>
public static class RuntimeFormatSkillText
{
    /// <summary>The skill-relative files that hold generated regions, under <c>.claude/skills/</c>.</summary>
    public static readonly string[] Files =
    {
        "skypatcher-authoring/SKILL.md",
        "skypatcher-authoring/references/placement-and-conflicts.md",
        "skypatcher-authoring/references/grammar-core.md",
        "spid-authoring/references/grammar-core.md",
        "spid-authoring/references/value-tables.md",
    };

    /// <summary>The opening marker of a region; the closing one is <see cref="End"/>.</summary>
    public static string Begin(string tag) => $"<!-- generated:{tag} -->";

    /// <summary>The closing marker of a region.</summary>
    public static string End(string tag) => $"<!-- end generated:{tag} -->";

    /// <summary>Every region's text by tag, printed from SkyPatcher's row.</summary>
    public static IReadOnlyDictionary<string, string> Regions(SkyPatcherFormat f)
    {
        var g = f.Grammar;
        string tok = f.NameToken, TOK = f.NameToken.ToUpperInvariant();
        var ext = f.GateExtensions;
        var root = f.Root.Replace('\\', '/');
        var early = f.Passes.Where(p => p.Runs == SkyPatcherPassRuns.Early).ToList();
        var noEffect = f.Passes.Where(p => p.Runs == SkyPatcherPassRuns.NoEffect).ToList();
        string Key(string folder) => f.SwitchKeyPrefix + char.ToUpperInvariant(folder[0]) + folder[1..] + f.SwitchKeySuffix;
        var onWithout = Join(f.OnWithoutKey.Select(k => $"`{Key(k)}`").ToList(), "and");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["skypatcher.place"] =
                $"The INI goes at `Data/{root}/<subfolder>/<name>{tok}`, with `<subfolder>` taken from\n" +
                $"the table. A plain filename always loads; a filename that matches a plugin (`Plugin{ext[0]}{tok}`) loads\n" +
                "only when that plugin is active, so it self-gates — the right shape for a patch against one mod, the\n" +
                "wrong shape when the targets span many plugins. Nest a plugin-named INI in a mod-specific subfolder\n" +
                $"so another mod shipping the same filename cannot overwrite it. Any name with `{tok}` in it is read\n" +
                $"(`x{tok}.bak` too); a name ending `{TOK}` is not. Comments start with `{g.Comment}`.",

            ["skypatcher.tree"] = Tree(f),

            ["skypatcher.names"] =
                $"SkyPatcher reads every file that has `{tok}` anywhere in its name, so backups like\n" +
                $"`bandits{tok}.bak` or `bandits{tok}.off` are read too. Write the ending `{tok}` in lower case: a file\n" +
                $"ending in `{TOK}` is not read.\n" +
                "\n" +
                "| Filename shape | When it loads |\n" +
                "|---|---|\n" +
                $"| `anything{tok}` (e.g. `myEdits{tok}`) | **Always** loaded. |\n" +
                $"| `Plugin{ext[1]}{tok}` / `Plugin{ext[0]}{tok}` (name = a plugin filename) | **Only** when that plugin is active in the load order; otherwise skipped. |\n" +
                $"| `SomeMod{ext[0]}-extra{tok}` (a plugin name with more text before `{tok}`) | **Never** read: the name before `{tok}` must be exactly the plugin name. |\n" +
                "\n" +
                $"Upper and lower case of the plugin name do not matter, also not for the ending {string.Join(", ", ext.Take(ext.Count - 1).Select(e => $"`{e}`"))} or\n" +
                $"`{ext[^1]}` (an upper-case ending works since 8.0.1).{string.Concat(early.Select(p => $" The same rule counts for the `{p.Folder}` folder."))}",

            ["skypatcher.conflict"] =
                "Each patcher reads its own type folder. First it reads the files of the type folder itself, then\n" +
                "the files of its subfolders, then the subfolders inside those, level by level. Files in one folder\n" +
                "are read in the order Windows lists them, which is A to Z on normal drives. When two lines set the\n" +
                $"**same value** of the same record, the line read last wins (`zPatch{tok}` beats `mPatch{tok}` in one\n" +
                $"folder; any file of `armor/` beats every file of `armor/MyMod/`).\n" +
                "Add/remove operations (e.g. `keywordsToAdd`, `formsToAdd`) **accumulate** rather than\n" +
                "overwrite, so multiple INIs can add to the same record without conflict.",

            ["skypatcher.switches"] =
                $"- **`[{f.SwitchesSection}]`** — one `{f.SwitchKeyPrefix}<Type>{f.SwitchKeySuffix}=1|0` per record type. Without the key a patcher is\n" +
                $"  **off**; only {onWithout} {(f.OnWithoutKey.Count == 1 ? "is" : "are")} on without the key. Keep the shipped file installed:\n" +
                "  without it almost every patcher stays off. Turning a type off skips its whole subfolder.\n" +
                $"  {string.Join(" ", noEffect.Select(p => $"`{Key(p.Folder)}` has no effect in Skyrim;"))}" +
                $"{string.Concat(early.Select(p => $" {p.Name} needs\n  `{Key(p.Folder)}=1`."))}",

            ["skypatcher.passes"] = Wrap(
                "Patchers run in a fixed order, and a later patcher sees the changes of an earlier one: " +
                string.Join(", ", f.Passes.Where(p => p.Runs == SkyPatcherPassRuns.InOrder).Select(p => p.Name)) + "." +
                string.Concat(early.Select(p => $" The {p.Name} patcher {p.Note}.")), 100),

            ["skypatcher.segments"] =
                "One line = one patch string, built from a **filter** part and an **operation** part, joined\n" +
                $"by `{g.Segment}` segments:\n" +
                "\n" +
                "```\n" +
                $"filter1{g.Assign}val1 {g.Segment} filter2{g.Assign}val2 {g.Segment} op1{g.Assign}val1 {g.Segment} op2{g.Assign}val2\n" +
                "```\n" +
                "\n" +
                $"- `{g.Segment}` separates every segment (filter or operation).\n" +
                "- Strings are **modular** — include only the segments you need. A line with no operation does\n" +
                "  nothing; a line with no filter targets *all* records of that type, with the exceptions in §5.\n" +
                $"- A space after a comma is fine; do not put spaces around `{g.Address}` (§4). A value can never contain `{g.Segment}`;\n" +
                $"  only a name written between `{g.Compound}` and `{g.Compound}` can.\n" +
                "- Write each key once per line: a second copy of the same key is ignored.\n" +
                $"- A comment starts with `{g.Comment}`." +
                (g.TrailingComment ? $" Since 8.0.1 a comment may also follow a rule on the same line\n  (`level{g.Assign}10 {g.Comment} Nazeem`)." : ""),
        };
    }

    /// <summary>Every region's text by tag, SkyPatcher's and SPID's, from the shipped source.</summary>
    public static IReadOnlyDictionary<string, string> Regions()
        => Regions(RuntimeFormats.SkyPatcher).Concat(Regions(RuntimeFormats.Spid)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>SPID's regions, printed from its distributor row: discovery from its folder, the rest from its lines block.</summary>
    public static IReadOnlyDictionary<string, string> Regions(DistributorFormat spid)
    {
        var folder = spid.Folders.Single();
        var g = spid.Lines ?? throw new InvalidOperationException($"runtime-format source: the {spid.Key} row has no lines block to print.");
        var t = spid.Targets;
        string tok = folder.NameToken, ext = folder.Extension;
        string Case(StringComparison c) => c == StringComparison.Ordinal ? "case-sensitive" : "any case";
        var typed = g.Types.Where(x => x.Runs != SpidType.Inferred).ToList();
        var form = g.Types.Single(x => x.Runs == SpidType.Inferred);
        var numbered = typed.Where(x => x.Runs != SpidType.OwnPass).OrderBy(x => x.Runs).Select(x => x.Name).ToList();
        var ownPass = typed.Where(x => x.Runs == SpidType.OwnPass).Select(x => x.Name).ToList();
        var first = typed.Where(x => x.Conflict == SpidConflict.FirstWins).Select(x => $"`{x.Key}`").ToList();
        var sums = typed.Where(x => x.Conflict == SpidConflict.Sums).Select(x => $"`{x.Key}`").ToList();
        var creates = g.Types.Where(x => x.MissingValue == SpidMissingValue.CreatesKeyword).Select(x => $"`{x.Key}`").ToList();
        var handsNothing = g.Types.Where(x => x.MissingValue == SpidMissingValue.CreatesKeywordHandsNothing).Select(x => $"`{x.Key}`").ToList();
        string Fields(SpidLineKind k) => string.Join($" {g.Sections} ", g.Kind(k).Fields.Select(FieldWord));
        var regular = g.Kind(SpidLineKind.Regular);
        var death = g.Kind(SpidLineKind.Death);
        var linked = g.Kind(SpidLineKind.Linked);
        var group = g.Kind(SpidLineKind.ExclusiveGroup);
        var models = string.Join(", ", g.Reads.Where(v => v != spid.Version));
        var leads = Join(g.CommentLeads.Select(c => $"`{c}`").ToList(), "or");
        var sep = t.Separator;
        var plugin = Join(t.PluginExtensions.Select(e => $"`{e}`").ToList(), "or");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spid.discovery"] = Wrap(
                $"SPID reads every file directly in `Data/` whose name holds `{tok}` anywhere ({Case(folder.TokenCompare)}: " +
                $"`{tok.ToLowerInvariant()}` is not read, and `Foo{tok}_old{ext}` is) and whose extension is exactly `{ext}` " +
                $"({Case(folder.ExtensionCompare)}: `{ext.ToUpperInvariant()}` is not read). A file in a subfolder is never read, nor one " +
                "that is only inside a BSA. Files are read in the byte order of their names, not alphabetically: upper-case letters " +
                $"sort before lower-case ones and `_` sits between them, so `NPC_Trap{tok}{ext}` comes before `Naked{tok}{ext}`. " +
                $"These paragraphs model SPID {spid.Version}; {models} reads its files the same.", 100),

            ["spid.lines"] = Wrap(
                "Only the lines before the first `[section]` header are read. A line whose first non-space character is " +
                $"{leads} is a comment. There is no comment after a value: everything after `{g.Assign}` to the end of the line is " +
                $"the value, so `Spell = X ; note` names `X ; note`. A key is `<Type>`, `{g.FinalPrefix}<Type>` (" +
                $"{Join(g.FinalTypes.Select(x => $"`{x}`").ToList(), "and")} only; on another type SPID drops `{g.FinalPrefix}`), " +
                $"`{death.Prefix}<Type>` (handed out when the NPC dies), `{linked.Global}{linked.Prefix}<Type>` or `{linked.Prefix}<Type>` " +
                $"(optionally with `{g.FinalPrefix}` and `{death.Prefix}` after `{linked.Prefix}`), or `{group.Key}`. `<Type>` is one of " +
                $"{string.Join(", ", g.Types.Select(x => $"`{x.Key}`"))}, spelt in exactly that case; any other key is skipped. " +
                $"A regular or `{death.Prefix}` line is `{Fields(regular.Kind)}`; a `{linked.Prefix}` line is `{Fields(linked.Kind)}`; " +
                $"an `{group.Key}` line is `{Fields(group.Kind)}`. A line with more `{g.Sections}` sections than that is skipped. An " +
                $"empty section, or `{g.Unset}` in any case, is unset, but only as the whole section: `{g.Unset}` as one item of a list " +
                $"is looked up as a name. In a filter section `{g.Item}` separates the items (OR), `{g.All}` joins the parts of one " +
                $"item (AND), a leading `{g.Not}` excludes, and in a string filter a leading `{g.Partial}` matches part of a name. An " +
                "empty item is skipped with its whole line.", 100),

            ["spid.names"] = Wrap(
                $"In any form position a token holding `{sep}` is a FormID with its plugin (`0x800{sep}Plugin.esp`): the number is " +
                "added to the plugin's own load index as the game numbers it, so a load index written into the number moves it to " +
                $"another plugin's record. Else a token holding {plugin} is a plugin name; in the form filters it selects every NPC " +
                "that plugin defines. Else `0x` followed by hex digits is a runtime FormID (`0x1CE14`, as the console prints it, so it " +
                "depends on the load order). Anything else is an EditorID, looked up among every record type; `12345` without `0x` is " +
                "an EditorID too.", 100),

            ["spid.missing"] = Wrap(
                $"A value name no record holds: on a {Join(creates, "or")} line SPID creates a keyword of that name and hands it out; " +
                $"on a {Join(handsNothing, "or")} line it creates the keyword and hands nothing out; on every other type SPID drops " +
                "the line, as it does for a FormID or a plugin that names nothing. In the form filters a token that names nothing, or " +
                "a record type SPID does not take as a filter, is skipped and the line reaches more NPCs (or, in the " +
                $"`{g.Not}` list, excludes fewer), unless every token of that list fails: then the line is dropped. One `{g.All}` part " +
                "that names nothing drops the line. A filter never creates a keyword.", 100),

            ["spid.order"] = Wrap(
                "Within one file SPID takes the lines grouped by key, the keys in alphabetical order whatever their case, and each " +
                $"key's lines in file order, so a file's `{g.FinalPrefix}Outfit` lines come before its `Outfit` lines wherever they " +
                "sit. Each line joins its type's list: the files in their read order, each with its lines in that order. " +
                $"`{form.Key}` lines join after every typed line of every file. Types are handed out in this order: " +
                $"{string.Join(" → ", numbered)}, then {Join(ownPass, "and")} in a pass of their own. Keyword lines are re-sorted so a " +
                "keyword another keyword line's string filter names is handed out first. " +
                $"For {Join(first, "and")} the first line whose filters pass an NPC wins it and the rest are not tried; for every " +
                $"other type each passing line gives{(sums.Count > 0 ? $", and {Join(sums, "and")} counts add up" : "")}.", 100),

            ["spid.runs"] =
                "```\n" + string.Join(" → ", numbered) + " → (own pass) " + string.Join(", ", ownPass) + "\n```\n" +
                "\n" +
                Wrap($"Within a type: the files in byte order of their names, each file's lines by key (any case) then file order, " +
                     $"`{form.Key}` lines last. For {Join(first, "and")} the first passing line wins.", 100),
        };
    }

    /// <summary>A field's name as the printed line shapes spell it.</summary>
    static string FieldWord(SpidField f) => f switch
    {
        SpidField.Value => "Form",
        SpidField.Strings => "StringFilters",
        SpidField.Forms => "FormFilters",
        SpidField.Level => "LevelFilters",
        SpidField.Traits => "TraitFilters",
        SpidField.CountOrIndex => "CountOrIndex",
        SpidField.Chance => "Chance",
        SpidField.Name => "GroupName",
        SpidField.Members => "Forms",
        SpidField.Parents => "ParentForms",
        _ => f.ToString(),
    };

    /// <summary>The folder tree: the root's parents, the DLL and the switches file beside the root, then example type folders.</summary>
    static string Tree(SkyPatcherFormat f)
    {
        var parts = f.Root.Split('\\');
        var switchParts = f.SwitchesFile.Split('\\');
        if (!parts[..^1].SequenceEqual(switchParts[..^1]))
            throw new InvalidOperationException($"runtime-format source: the switches file '{f.SwitchesFile}' is not beside the root '{f.Root}', and the tree has no shape for that.");
        var sb = new StringBuilder("```\nData/\n");
        for (int i = 0; i < parts.Length - 1; i++) sb.Append(new string(' ', 4 * i)).Append("└── ").Append(parts[i]).Append("/\n");
        var at = new string(' ', 4 * (parts.Length - 1));
        sb.Append(at).Append("├── ").Append(parts[^1]).Append(".dll\n");
        sb.Append(at).Append("├── ").Append(switchParts[^1].PadRight(26)).Append("← global settings (§6)\n");
        sb.Append(at).Append("└── ").Append(parts[^1]).Append("/\n");
        var inner = at + "    ";
        sb.Append(inner).Append("├── ").Append("npc/".PadRight(23)).Append("← one subfolder per record type\n");
        sb.Append(inner).Append("├── weapon/\n");
        sb.Append(inner).Append("├── armor/\n");
        sb.Append(inner).Append("└── … (the subfolder column of the router table in SKILL.md)\n```");
        return sb.ToString();
    }

    /// <summary>A list in prose: "a", "a or b", "a, b or c".</summary>
    static string Join(IReadOnlyList<string> xs, string last)
        => xs.Count <= 1 ? string.Concat(xs) : string.Join(", ", xs.Take(xs.Count - 1)) + $" {last} " + xs[^1];

    /// <summary>Greedy word wrap at <paramref name="width"/> columns.</summary>
    static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        int col = 0;
        foreach (var w in text.Split(' '))
        {
            if (col > 0 && col + 1 + w.Length > width) { sb.Append('\n'); col = 0; }
            else if (col > 0) { sb.Append(' '); col++; }
            sb.Append(w);
            col += w.Length;
        }
        return sb.ToString();
    }

    const string Open = "<!-- generated:";

    /// <summary>The tag a line opens a region for: the whole line is <see cref="Begin"/> of it; null for any other line.</summary>
    static string? Opens(string line)
    {
        if (!line.StartsWith(Open, StringComparison.Ordinal)) return null;
        int end = line.IndexOf(' ', Open.Length);
        var tag = end < 0 ? "" : line[Open.Length..end];
        return line == Begin(tag) ? tag : null;
    }

    /// <summary>Replace every marked region of one file's text with its printed text, keeping the file's line ending; a marker that is not a whole-line opener, an unknown tag or an unclosed region throws.</summary>
    public static string Rewrite(string text, IReadOnlyDictionary<string, string> regions, string label)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl);
        var output = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            output.Add(lines[i]);
            if (!lines[i].Contains(Open, StringComparison.Ordinal)) continue;
            if (Opens(lines[i]) is not { } tag || !regions.TryGetValue(tag, out var body))
                throw new InvalidOperationException($"{label}: line {i + 1} opens a region the generator has no text for, or is not a whole-line region marker: '{lines[i]}'.");
            int close = Array.IndexOf(lines, End(tag), i + 1);
            if (close < 0) throw new InvalidOperationException($"{label}: region '{tag}' opened at line {i + 1} is never closed.");
            output.AddRange(body.Split('\n'));
            output.Add(lines[close]);
            i = close;
        }
        return string.Join(nl, output);
    }

    /// <summary>The region tags no line of <paramref name="texts"/> opens, by the rule <see cref="Rewrite"/> uses.</summary>
    public static IReadOnlyList<string> Unplaced(IReadOnlyDictionary<string, string> regions, IEnumerable<string> texts)
    {
        var opened = texts.SelectMany(x => x.Split('\n')).Select(l => Opens(l.TrimEnd('\r'))).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return regions.Keys.Where(t => !opened.Contains(t)).ToList();
    }

    /// <summary>Rewrite the regions in place under <paramref name="skillsDir"/>; returns how many files changed.</summary>
    public static int Apply(string skillsDir, IReadOnlyDictionary<string, string> regions)
    {
        var texts = Files.ToDictionary(rel => rel, rel => File.ReadAllText(Path.Combine(skillsDir, rel)));
        var unplaced = Unplaced(regions, texts.Values);
        if (unplaced.Count > 0) throw new InvalidOperationException($"no skill file holds region(s) {string.Join(", ", unplaced)}.");
        int changed = 0;
        foreach (var (rel, text) in texts)
        {
            var next = Rewrite(text, regions, rel);
            if (next != text) { File.WriteAllText(Path.Combine(skillsDir, rel), next); changed++; }
        }
        return changed;
    }

    /// <summary>The <c>skill-text [skillsDir]</c> mode: print the regions into the committed skill files.</summary>
    public static int Run(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : Path.Combine(".claude", "skills");
        if (!Directory.Exists(dir)) { Console.Error.WriteLine($"no skills directory at '{Path.GetFullPath(dir)}'; run from the repo root or pass it."); return 2; }
        int n = Apply(dir, Regions());
        Console.WriteLine($"skill-text: {n} file(s) rewritten under {Path.GetFullPath(dir)}.");
        return 0;
    }
}

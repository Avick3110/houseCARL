using System.Text;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Prints the skill paragraphs that state SkyPatcher's, SPID's and KID's discovery, order, conflict and grammar rules from the runtime-format source, into marked regions of the committed skill files; RuntimeFormatSourceTests holds the committed text to a fresh print.</summary>
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
        "kid-authoring/references/grammar-core.md",
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

    /// <summary>Every region's text by tag, SkyPatcher's, SPID's and KID's, from the shipped source.</summary>
    public static IReadOnlyDictionary<string, string> Regions()
        => Regions(RuntimeFormats.SkyPatcher).Concat(Regions(RuntimeFormats.Spid)).Concat(KidRegions(RuntimeFormats.Kid))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>KID's regions, printed from its distributor row: discovery from its folder, the rest from its lines block and targets.</summary>
    public static IReadOnlyDictionary<string, string> KidRegions(DistributorFormat kid)
    {
        var folder = kid.Folders.Single();
        var g = kid.KidLines ?? throw new InvalidOperationException($"runtime-format source: the {kid.Key} row has no KID lines block to print.");
        var t = kid.Targets;
        string tok = folder.NameToken, ext = folder.Extension;
        string Case(StringComparison c) => c == StringComparison.Ordinal ? "case-sensitive" : "any case";
        var models = string.Join(", ", g.Reads.Where(v => v != kid.Version));
        var leads = Join(g.CommentLeads.Select(c => $"`{c}`").ToList(), "or");
        var sep = t.Separator;
        var plugin = Join(t.PluginExtensions.Select(e => $"`{e}`").ToList(), "or");
        var traits = Join(g.Types.Where(x => x.Traits).Select(x => $"`{x.Key}`").ToList(), "and");
        var noTraits = Join(g.Types.Where(x => !x.Traits).Select(x => $"`{x.Key}`").ToList(), "and");
        var passes = string.Join(" → ", g.Types.OrderBy(x => x.Runs).Select(x => x.Key));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kid.discovery"] = Wrap(
                $"KID reads every file directly in `Data/` whose name holds `{tok}` anywhere ({Case(folder.TokenCompare)}: " +
                $"`{tok.ToLowerInvariant()}` is not read, nor `AllGear{tok.TrimStart('_')}{ext}`, which has no `{tok}`) and whose extension is exactly " +
                $"`{ext}` ({Case(folder.ExtensionCompare)}: `{ext.ToUpperInvariant()}` is not read). A file in a subfolder is never read, nor one " +
                "that is only inside a BSA. Files are read in the byte order of their names, not alphabetically: upper-case letters " +
                $"sort before lower-case ones and `_` sits between them, so `ZZZ{tok}{ext}` comes before `aaa{tok}{ext}`. " +
                $"These paragraphs model KID {kid.Version}; {models} reads its files the same.", 100),

            ["kid.lines"] = Wrap(
                "Only the unnamed section is read: the lines before the first named `[section]` header, and any after an empty " +
                $"`[]` header, which returns to it. A line whose first non-space character is {leads} is a comment. There is no " +
                $"comment after a value: everything after `{g.Assign}` to the end of the line is the value, so a `;` note after the " +
                "last section becomes part of it. The key " +
                $"`{g.GroupKey}`, spelt in exactly that case, makes a group line, `GroupName{g.Sections}Members`; any other key (`Keyword`, " +
                $"`keyword`, any word) makes a keyword line, `Keyword{g.Sections}Type{g.Sections}Filters{g.Sections}Traits{g.Sections}Chance`. " +
                $"The sections are split on `{g.Sections}` and not trimmed, so ` Armor` with a space is not a type. The type is one of " +
                $"{string.Join(", ", g.Types.Select(x => $"`{x.Key}`"))}, spelt exactly so; a line with any other type, or none, is " +
                "dropped without a word in the log. A section after the fifth is ignored. An empty section, or " +
                $"`{g.Unset}` in any case, is unset. In the filters `{g.Item}` separates the entries, each trimmed, and an entry " +
                $"`{g.Unset}` is skipped. An entry holding `{g.All}` is a group whose parts must all match (AND), the groups of a line " +
                $"are alternatives (OR), and the entries without `{g.All}` form one more list. A leading `{g.Not}` on an entry or on " +
                $"a group's part excludes, and after it a leading `{g.Partial}` makes the term a partial string. Traits are read on " +
                $"{traits} lines; on {noTraits} lines they are ignored. Each type reads its traits its own way (`traits.md`): a " +
                "trait no rule of the type takes is ignored, which widens the line, but some types match a trait by what it holds " +
                "(`R(`, `D(`, `W(`, digits only), and a soul gem line reads a trait that is not `BLACK`, `-BLACK` or `SOUL(` as the " +
                "gem's capacity when it holds a number (one with no number sets none, and clears one an earlier trait set), so a misspelt trait can read as " +
                "a different one and narrow the line (`XR(40)` on a magic effect line is a resistance filter). Chance is a number from 0 to 100, 100 when unset; a chance that does not start with a number " +
                $"fails the line, and KID logs `Failed to parse entry`. In a group line the members are split on `{g.Item}` and not " +
                $"trimmed, and a leading `{g.Not}` takes a keyword out of the group.", 100),

            ["kid.names"] = Wrap(
                $"A token holding exactly one `{sep}` is a FormID with its plugin (`0x800{sep}Plugin.esp`): the number is added to " +
                "the plugin's own load index as the game numbers it, so a load index written into the number moves it to another " +
                $"plugin's record, and a part before `{sep}` that is no hex number fails the line (`Failed to parse entry`). A token " +
                $"with two or more `{sep}` is a name. Else `0x` followed by hex digits only is a runtime FormID (`0x1CE14`, as the " +
                "console prints it, so it depends on the load order). Else a filter ending in " +
                $"{plugin} ({Case(StringComparison.Ordinal)}) is a plugin name and selects every item that plugin defines. Anything " +
                "else is a name. The keyword section's name is looked up among keywords only. A filter's name is looked up among " +
                "every record type, then among the keywords KID lines create, and only then kept as text. A filter ending in " +
                $"`{g.ModelEnding}`, in any case, is a model path.", 100),

            ["kid.missing"] = Wrap(
                "In the keyword section a name no keyword holds is created as a new keyword, also when a record of another type " +
                "holds the name; a FormID that names nothing, or names a record that is not a keyword, drops the line (`FAIL - " +
                "keyword doesn't exist`). Every line's keyword is created before any filter is read, so a line its filters drop " +
                "still creates it, and another line's filter can name it. In the filters a FormID, a runtime FormID or a plugin that " +
                "names nothing, or a record of a type KID does not take as a filter, is skipped and the line reaches more items " +
                $"(or, among the `{g.Not}` entries, excludes fewer), unless every plain entry without a leading `{g.Partial}` fails " +
                $"(a `{g.Partial}` entry does not keep the line), or every `{g.Not}` entry fails: then the line is dropped. A `{g.All}` group one part of which fails is dropped, and when every group is dropped, " +
                "so is the line. A filter name no record holds and no KID line creates is kept as text: it matches an item whose " +
                "EditorID or name is that text, or, for an actor-value or archetype name, whose own actor value or archetype it is. " +
                "A filter never creates a keyword.", 100),

            ["kid.order"] = Wrap(
                "Within one file KID takes the lines grouped by key, the keys in alphabetical order whatever their case, and each " +
                "key's lines in file order; the files are taken in their read order. KID then hands keywords out type by type, in " +
                $"this order: {passes}. Within a type KID re-sorts the keywords so that a keyword another line's filter names is " +
                "handed out first. KID only adds keywords: no line takes " +
                $"one off, and two lines never conflict, except that an item already holding one keyword of an `{g.GroupKey}` " +
                "receives no other keyword of that group.", 100),
        };
    }

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
                "Only the unnamed section is read: the lines before the first named `[section]` header, and any after an empty " +
                "`[]` header, which returns to it. A line whose first non-space character is " +
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
                $"item (AND), a leading `{g.Not}` excludes, and in a string filter a leading `{g.Partial}` matches part of an NPC's name, of its own or its template's EditorID " +
                "or of one of its keywords' EditorIDs (its race's and a keyword SPID creates included). An " +
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

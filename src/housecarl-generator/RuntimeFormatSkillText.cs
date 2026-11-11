using System.Text;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Prints the skill paragraphs that state SkyPatcher's discovery, order, conflict and grammar rules from the runtime-format source, into marked regions of the committed skill files; EmitMatchGuardTests holds the committed text to a fresh print.</summary>
public static class RuntimeFormatSkillText
{
    /// <summary>The skill-relative files that hold generated regions, under <c>.claude/skills/</c>.</summary>
    public static readonly string[] Files =
    {
        "skypatcher-authoring/SKILL.md",
        "skypatcher-authoring/references/placement-and-conflicts.md",
        "skypatcher-authoring/references/grammar-core.md",
    };

    /// <summary>The opening marker of a region; the closing one is <see cref="End"/>.</summary>
    public static string Begin(string tag) => $"<!-- generated:{tag} from the runtime-format source; edit the source and regenerate, not this text -->";

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
        string Ticked(IEnumerable<string> xs, string last) => Join(xs.Select(x => $"`{x}`").ToList(), last);

        var onWithout = Ticked(f.OnWithoutKey.Select(Key), "and");
        var regions = new Dictionary<string, string>(StringComparer.Ordinal)
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
        return regions;
    }

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

    /// <summary>Replace every marked region of one file's text with its printed text, keeping the file's line ending; an unknown tag or an unclosed region throws.</summary>
    public static string Rewrite(string text, IReadOnlyDictionary<string, string> regions, string label)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl);
        var output = new List<string>();
        const string open = "<!-- generated:";
        for (int i = 0; i < lines.Length; i++)
        {
            output.Add(lines[i]);
            if (!lines[i].StartsWith(open, StringComparison.Ordinal)) continue;
            var tag = lines[i][open.Length..lines[i].IndexOf(' ', open.Length)];
            if (lines[i] != Begin(tag) || !regions.TryGetValue(tag, out var body))
                throw new InvalidOperationException($"{label}: line {i + 1} opens a region the generator has no text for: '{lines[i]}'.");
            int close = Array.IndexOf(lines, End(tag), i + 1);
            if (close < 0) throw new InvalidOperationException($"{label}: region '{tag}' opened at line {i + 1} is never closed.");
            output.AddRange(body.Split('\n'));
            output.Add(lines[close]);
            i = close;
        }
        return string.Join(nl, output);
    }

    /// <summary>Rewrite the regions in place under <paramref name="skillsDir"/>; returns how many files changed.</summary>
    public static int Apply(string skillsDir, SkyPatcherFormat format)
    {
        var regions = Regions(format);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int changed = 0;
        foreach (var rel in Files)
        {
            var path = Path.Combine(skillsDir, rel);
            var text = File.ReadAllText(path);
            var next = Rewrite(text, regions, rel);
            foreach (var tag in regions.Keys) if (text.Contains(Begin(tag), StringComparison.Ordinal)) seen.Add(tag);
            if (next != text) { File.WriteAllText(path, next); changed++; }
        }
        var unplaced = regions.Keys.Where(t => !seen.Contains(t)).ToList();
        if (unplaced.Count > 0) throw new InvalidOperationException($"no skill file holds region(s) {string.Join(", ", unplaced)}.");
        return changed;
    }

    /// <summary>The <c>skill-text [skillsDir]</c> mode: print the regions into the committed skill files.</summary>
    public static int Run(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : Path.Combine(".claude", "skills");
        if (!Directory.Exists(dir)) { Console.Error.WriteLine($"no skills directory at '{Path.GetFullPath(dir)}'; run from the repo root or pass it."); return 2; }
        int n = Apply(dir, RuntimeFormats.SkyPatcher);
        Console.WriteLine($"skill-text: {n} file(s) rewritten under {Path.GetFullPath(dir)}.");
        return 0;
    }
}

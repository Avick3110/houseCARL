using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

// GameLanguage — the language one MO2 profile plays in (sLanguage in the INI the game reads) and where each plugin's
// string tables win across MO2's layers, resolved once per order build from the profile read that build makes, and
// refused in that same step. A tool call binds its service's answer to the call; every plugin open and write in the
// call takes it from there, never from a path. Contract in docs/architecture/load-order-resolver.md.

/// <summary>One order build's game language and the winning source of every string table its layers carry.</summary>
public sealed class InstanceText
{
    /// <summary>The answer outside any order build: English, tables beside the plugin else in the game Data, as houseCARL read before the language was resolved.</summary>
    public static readonly InstanceText English = new(Language.English, null, null, null);

    /// <summary>The language every plugin is read in and every new text is written in.</summary>
    public Language Language { get; }

    readonly Tables? _tables;
    readonly string? _dataDir;
    readonly HashSet<string>? _served;   // the order's plugin paths: the winners are theirs, an off-order copy reads beside itself

    InstanceText(Language language, Tables? tables, string? dataDir, HashSet<string>? served)
    { Language = language; _tables = tables; _dataDir = dataDir; _served = served; }

    internal static InstanceText Of(Language language, Tables tables, string dataDir, IEnumerable<string> served)
        => new(language, tables, dataDir, new HashSet<string>(served, StringComparer.OrdinalIgnoreCase));

    /// <summary>The winning sources of the plugin's tables in this language when it is the order's own copy: the loose
    /// Strings folder that wins, and the archive that wins where no loose copy does; null when no layer carries them,
    /// or when its kinds win from more than one folder, which Mutagen cannot read as one.</summary>
    internal ((string Dir, string Layer)? Loose, (string Path, string Layer)? Archive)? WinnersFor(string pluginPath)
    {
        if (_tables is null || _served?.Contains(pluginPath) != true) return null;
        var w = Combined(Path.GetFileNameWithoutExtension(pluginPath));
        return w.Split is not null || w.Loose is null && w.Archive is null ? null : (w.Loose, w.Archive);
    }

    /// <summary>The plugin's kinds' winners as the one loose Strings folder and one archive Mutagen reads them from, and
    /// the split, in words, when its kinds win from different loose folders or from archives in different folders.</summary>
    internal ((string Dir, string Layer)? Loose, (string Path, string Layer)? Archive, string? Split) Combined(string stem)
    {
        var kinds = LocalizedStrings.Kinds.Select(k => (Kind: k, W: Winner(stem, k))).ToList();
        var loose = kinds.Where(x => x.W.Loose is not null).Select(x => x.W.Loose!.Value).ToList();
        var archived = kinds.Where(x => x.W.Loose is null && x.W.Archive is not null).Select(x => x.W.Archive!.Value).ToList();
        bool split = loose.Select(l => l.Dir).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                  || archived.Select(a => Path.GetDirectoryName(a.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var words = kinds.Where(x => x.W.Loose is not null || x.W.Archive is not null)
                         .Select(x => x.W.Loose is { } l ? $".{x.Kind} loose {GameLanguage.LayerPlace(l.Layer)}"
                                                         : $".{x.Kind} in {Path.GetFileName(x.W.Archive!.Value.Path)} {GameLanguage.LayerPlace(x.W.Archive.Value.Layer)}");
        return (loose.Count > 0 ? loose[0] : null, archived.Count > 0 ? archived[0] : null, split ? string.Join(", ", words) : null);
    }

    /// <summary>One table kind's winner: the loose copy, else the highest-ranked archive Mutagen opens for the plugin
    /// (one the INI lists, or one named for it); and the archives carrying it that Mutagen would not open for it.</summary>
    internal ((string Dir, string Layer)? Loose, (string Path, string Layer)? Archive, List<(string Path, string Layer)> Passed) Winner(string stem, string kind)
    {
        var file = $"{stem}_{Language}.{kind}";
        var passed = new List<(string, string)>();
        if (_tables is null) return (null, null, passed);
        if (_tables.Loose.TryGetValue(file, out var dir)) return (dir, null, passed);
        if (_tables.Archives.TryGetValue(file, out var ranked))
            foreach (var a in ranked)
            {
                if (a.IniListed || NamedFor(stem, a.Path)) return (null, (a.Path, a.Layer), passed);
                passed.Add((a.Path, a.Layer));
            }
        return (null, null, passed);
    }

    /// <summary>The Strings folder and archive folder Mutagen reads the plugin's tables from, or null to leave them
    /// beside the plugin. Winners first; a plugin no layer carries tables for, or a copy the order does not serve,
    /// keeps the old rule: beside it when its folder has them (in this language, any language for English, as
    /// before), else the game Data.</summary>
    internal (string Strings, string Archives)? FoldersFor(string pluginPath, string? dataDir)
    {
        var data = _dataDir ?? dataDir;
        if (WinnersFor(pluginPath) is { } w)
        {
            var archives = w.Archive is { } a ? Path.GetDirectoryName(a.Path)! : w.Loose is { } l ? Path.GetDirectoryName(l.Dir)! : data;
            var strings = w.Loose?.Dir ?? (data is null ? null : Path.Combine(data, "Strings"));
            if (strings is not null && archives is not null) return (strings, archives);
        }
        if (data is null || LocalizedStrings.OwnFolderCarriesStringsFor(pluginPath, Language)) return null;
        return (Path.Combine(data, "Strings"), data);
    }

    /// <summary>Mutagen's rule for an archive named for a plugin: its name, or its name before the last " - ", is the plugin's.</summary>
    static bool NamedFor(string stem, string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var cut = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return name.Equals(stem, StringComparison.OrdinalIgnoreCase)
            || cut >= 0 && name[..cut].Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every string-table file name the order's layers carry: the loose winner's Strings folder and its layer, and the loaded archives holding it, highest rank first.</summary>
    internal sealed record Tables(Dictionary<string, (string Dir, string Layer)> Loose,
                                  Dictionary<string, List<(string Path, string Layer, bool IniListed)>> Archives);
}

public static class GameLanguage
{
    /// <summary>Read the order's language from the INI the game reads and its string-table winners from the same
    /// composition and archive list the build uses; throws <see cref="GameLanguageRefusedException"/> when the
    /// language is not one the game has, or when Skyrim.esm lacks any of its three tables in it.</summary>
    public static InstanceText Read(GameIni ini, Mo2Composition comp, ArchiveDiscoveryResult discovery,
                                    string modsDir, string dataDir, string overwriteDir, IReadOnlyList<string> orderedPaths)
    {
        var archives = discovery.Archives;
        var (raw, file) = ini.Value("General", "sLanguage");
        var language = Language.English;
        if (raw is not null && (!raw.All(char.IsLetter) || !Enum.TryParse(raw, ignoreCase: true, out language)))
            throw new GameLanguageRefusedException(
                $"houseCARL cannot read this load order: {file} sets sLanguage={raw}, which is not a game language it "
                + "knows; set it to the language your game is installed in (ENGLISH, GERMAN, FRENCH, RUSSIAN, …).");

        var text = InstanceText.Of(language, ReadTables(language, comp.EnabledMods, modsDir, dataDir, overwriteDir, archives), dataDir, orderedPaths);
        var skyrimPath = orderedPaths.FirstOrDefault(p => Path.GetFileName(p).Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase));
        // English is what houseCARL always read, so only another language is held to having its tables.
        if (language == Language.English || skyrimPath is null
            || WriteEngine.PluginIsLocalized(skyrimPath) != LocalizedFlagRead.Localized) return text;
        var winners = LocalizedStrings.Kinds.Select(k => (Kind: k, W: text.Winner("Skyrim", k))).ToList();
        var missing = winners.Where(x => x.W.Loose is null && x.W.Archive is null).ToList();
        if (missing.Count == 0)
        {
            if (text.Combined("Skyrim").Split is not { } split) return text;
            throw new GameLanguageRefusedException(
                $"houseCARL cannot read this load order in {language}: {file} sets sLanguage={raw}, and Skyrim.esm's "
                + $"{language} string tables win from more than one folder ({split}), but a plugin's tables are read from "
                + "one folder, so the rest would read blank; put all three in one mod or archive.");
        }
        var passed = missing.SelectMany(x => x.W.Passed).DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                            .Select(p => $"{Path.GetFileName(p.Path)} {LayerPlace(p.Layer)}").ToList();
        throw new GameLanguageRefusedException(
            $"houseCARL cannot read this load order in {language}: {file} sets sLanguage={raw}, and Skyrim.esm's "
            + $"{language} .{string.Join("/.", missing.Select(x => x.Kind))} string tables were not found loose in a Strings folder or in an "
            + "archive the game loads for Skyrim.esm (one the INI lists, or one named for it) in the game Data, an enabled "
            + "mod or the overwrite folder"
            + (passed.Count == 0 ? "" : $"; {string.Join(", ", passed)} carries them, but it is named for another plugin "
                                        + "and the INI does not list it, so houseCARL cannot read them from it")
            + "; install that language's files or set sLanguage to the language your game is installed in."
            + (discovery.Warnings.Count == 0 ? "" : " Also, " + string.Join(" ", discovery.Warnings)));
    }

    /// <summary>Where an MO2 layer is, as a refusal names it.</summary>
    internal static string LayerPlace(string layer) => layer switch
    {
        AssetResolver.DataLayerName => "in the game Data",
        AssetResolver.OverwriteLayerName => "in MO2's overwrite folder",
        _ => $"in the mod folder '{layer}'",
    };

    /// <summary>Every table file in a loose Strings folder (overwrite, enabled mods by priority, game Data: first wins)
    /// and in a loaded archive (highest plugin rank first, archive name breaking a tie), the asset layer's precedence; the order's language only.</summary>
    static InstanceText.Tables ReadTables(Language language, IReadOnlyList<string> enabledMods, string modsDir, string dataDir, string overwriteDir,
                                          IReadOnlyList<ActiveArchive> archives)
    {
        var loose = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Root, string Layer)>(enabledMods.Count + 2);
        if (overwriteDir.Length > 0) roots.Add((overwriteDir, AssetResolver.OverwriteLayerName));
        roots.AddRange(enabledMods.Select(m => (Path.Combine(modsDir, m), m)));
        if (dataDir.Length > 0) roots.Add((dataDir, AssetResolver.DataLayerName));
        foreach (var (root, layer) in roots)
        {
            var dir = Path.Combine(root, "Strings");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, $"*_{language}.*strings"))
                    loose.TryAdd(Path.GetFileName(f), (dir, layer));
            }
            catch (IOException) { }                     // a Strings folder that will not list adds nothing, as the game sees none
            catch (UnauthorizedAccessException) { }
        }

        var byFile = new Dictionary<string, List<(string Path, string Layer, bool IniListed, int Rank)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in archives.GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
        {
            var a = g.MaxBy(a => a.PluginRank)!;
            var iniListed = g.Any(x => x.OwningPlugin == ArchiveDiscovery.IniArchiveOwner);
            foreach (var entry in LocalizedStrings.ArchiveTableFiles(a.Path).Where(e => e.Contains($"_{language}.", StringComparison.OrdinalIgnoreCase)))
            {
                if (!byFile.TryGetValue(entry, out var list)) byFile[entry] = list = new();
                list.Add((a.Path, a.OwningMod ?? AssetResolver.DataLayerName, iniListed, a.PluginRank));
            }
        }
        var ranked = byFile.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Rank).ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                          .Select(x => (x.Path, x.Layer, x.IniListed)).ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new InstanceText.Tables(loose, ranked);
    }

    sealed class Bound { public volatile InstanceText? Text; }

    static readonly AsyncLocal<Bound?> Call = new();

    /// <summary>Start one tool call's binding; a nested tool body shares the outer call's. Dispose ends it.</summary>
    public static Scope Begin()
    {
        var outer = Call.Value;
        if (outer is null) Call.Value = new Bound();
        return new Scope(outer is null);
    }

    public readonly struct Scope(bool owns) : IDisposable
    {
        public void Dispose() { if (owns) Call.Value = null; }
    }

    /// <summary>Bind the service's current order build to this call, so every open and write in it takes that language and those winners.</summary>
    public static void Bind(InstanceText? text)
    {
        if (text is not null && Call.Value is { } call) call.Text = text;
    }

    /// <summary>This call's language and table winners, read once per open; <see cref="InstanceText.English"/> outside a bound call.</summary>
    public static InstanceText Current => Call.Value?.Text ?? InstanceText.English;
}

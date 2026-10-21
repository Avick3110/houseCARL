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
    public static readonly InstanceText English = new(Language.English, null, null);

    /// <summary>The language every plugin is read in and every new text is written in.</summary>
    public Language Language { get; }

    readonly Tables? _tables;
    readonly string? _dataDir;

    InstanceText(Language language, Tables? tables, string? dataDir) { Language = language; _tables = tables; _dataDir = dataDir; }

    internal static InstanceText Of(Language language, Tables tables, string dataDir) => new(language, tables, dataDir);

    /// <summary>The winning sources of the plugin's tables in this language: the loose Strings folder that wins, and the
    /// archive that wins where no loose copy does; null when no layer of the order carries them.</summary>
    internal (string? Loose, (string Path, string Layer)? Archive)? WinnersFor(string stem)
    {
        if (_tables is null) return null;
        string? loose = null;
        (string, string)? archive = null;
        foreach (var kind in LocalizedStrings.Kinds)
        {
            var file = $"{stem}_{Language}.{kind}";
            if (_tables.Loose.TryGetValue(file, out var dir)) loose ??= dir;
            else if (archive is null && _tables.Archives.TryGetValue(file, out var ranked))
                // Mutagen opens a mod folder's archive only when it is named for the plugin; the game Data's by its own rules.
                foreach (var a in ranked)
                    if (a.Layer == AssetResolver.DataLayerName || NamedFor(stem, a.Path)) { archive = (a.Path, a.Layer); break; }
        }
        return loose is null && archive is null ? null : (loose, archive);
    }

    /// <summary>The Strings folder and archive folder Mutagen reads the plugin's tables from, or null to leave them
    /// beside the plugin. Winners first; a plugin no layer carries tables for keeps the old rule: beside it when its
    /// folder has them (in this language, any language for English, as before), else the game Data.</summary>
    internal (string Strings, string Archives)? FoldersFor(string pluginPath, string? dataDir)
    {
        var data = _dataDir ?? dataDir;
        if (WinnersFor(Path.GetFileNameWithoutExtension(pluginPath)) is { } w)
        {
            var archives = w.Archive is { } a ? Path.GetDirectoryName(a.Path)! : w.Loose is { } l ? Path.GetDirectoryName(l)! : data;
            var strings = w.Loose ?? (data is null ? null : Path.Combine(data, "Strings"));
            if (strings is not null && archives is not null) return (strings, archives);
        }
        if (data is null || LocalizedStrings.OwnFolderCarriesStringsFor(pluginPath, Language)) return null;
        return (Path.Combine(data, "Strings"), data);
    }

    static bool NamedFor(string stem, string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        return name.Equals(stem, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every string-table file name the order's layers carry: the loose winner's Strings folder, and the loaded archives holding it, highest rank first.</summary>
    internal sealed record Tables(Dictionary<string, string> Loose, Dictionary<string, List<(string Path, string Layer)>> Archives);
}

public static class GameLanguage
{
    /// <summary>Read the order's language from the INI the game reads and its string-table winners from the same
    /// composition and archive list the build uses; throws <see cref="GameLanguageRefusedException"/> when the
    /// language is not one the game has, or when no layer carries Skyrim.esm's tables in it.</summary>
    public static InstanceText Read(GameIni ini, Mo2Composition comp, IReadOnlyList<ActiveArchive> archives,
                                    string modsDir, string dataDir, string overwriteDir, string? skyrimPath)
    {
        var (raw, file) = ini.Value("General", "sLanguage");
        var language = Language.English;
        if (raw is not null && (!raw.All(char.IsLetter) || !Enum.TryParse(raw, ignoreCase: true, out language)))
            throw new GameLanguageRefusedException(
                $"houseCARL cannot read this load order: {file} sets sLanguage={raw}, which is not a game language it "
                + "knows; set it to the language your game is installed in (ENGLISH, GERMAN, FRENCH, RUSSIAN, …).");

        var text = InstanceText.Of(language, ReadTables(language, comp.EnabledMods, modsDir, dataDir, overwriteDir, archives), dataDir);
        // English is what houseCARL always read, so only another language is held to having its tables.
        if (language != Language.English && skyrimPath is not null
            && WriteEngine.PluginIsLocalized(skyrimPath) == LocalizedFlagRead.Localized && text.WinnersFor("Skyrim") is null)
            throw new GameLanguageRefusedException(
                $"houseCARL cannot read this load order in {language}: {file} sets sLanguage={raw}, and no {language} "
                + "string tables for Skyrim.esm were found loose in a Strings folder or in a loaded archive of the game "
                + "Data, an enabled mod or the overwrite folder; install that language's files or set sLanguage to the "
                + "language your game is installed in.");
        return text;
    }

    /// <summary>Every table file in a loose Strings folder (overwrite, enabled mods by priority, game Data: first wins)
    /// and in a loaded archive (highest plugin rank first, archive name breaking a tie), the asset layer's precedence; the order's language only.</summary>
    static InstanceText.Tables ReadTables(Language language, IReadOnlyList<string> enabledMods, string modsDir, string dataDir, string overwriteDir,
                                          IReadOnlyList<ActiveArchive> archives)
    {
        var loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>(enabledMods.Count + 2);
        if (overwriteDir.Length > 0) roots.Add(overwriteDir);
        roots.AddRange(enabledMods.Select(m => Path.Combine(modsDir, m)));
        if (dataDir.Length > 0) roots.Add(dataDir);
        foreach (var root in roots)
        {
            var dir = Path.Combine(root, "Strings");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, $"*_{language}.*strings"))
                    loose.TryAdd(Path.GetFileName(f), dir);
            }
            catch (IOException) { }                     // a Strings folder that will not list adds nothing, as the game sees none
            catch (UnauthorizedAccessException) { }
        }

        var byFile = new Dictionary<string, List<(string Path, string Layer, int Rank)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives.GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(a => a.PluginRank)!))
            foreach (var entry in LocalizedStrings.ArchiveTableFiles(a.Path).Where(e => e.Contains($"_{language}.", StringComparison.OrdinalIgnoreCase)))
            {
                if (!byFile.TryGetValue(entry, out var list)) byFile[entry] = list = new();
                list.Add((a.Path, a.OwningMod ?? AssetResolver.DataLayerName, a.PluginRank));
            }
        var ranked = byFile.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Rank).ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                          .Select(x => (x.Path, x.Layer)).ToList(),
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

using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

// GameLanguage — the language one MO2 instance plays in (Skyrim.ini sLanguage) and where a plugin whose string tables
// are not beside it finds its strings archive. Read once per resolver build, registered by the instance's roots, and
// looked up by plugin path at PluginTextEncoding, the door every plugin open and write passes. Contract in
// docs/architecture/load-order-resolver.md.

/// <summary>One instance's game language, its refusal when that language has no string tables, and its archive winners.</summary>
public sealed class InstanceText
{
    /// <summary>The language every plugin under this instance is opened in; English when the ini names none.</summary>
    public Language Language { get; }

    /// <summary>The one sentence every open under this instance refuses with, or null when the language reads.</summary>
    public string? Refusal { get; }

    internal readonly string[] Roots;
    readonly string _dataDir;
    readonly Lazy<Dictionary<string, (string Path, string OwningMod)>> _archives;

    internal InstanceText(Language language, string? refusal, string[] roots, string dataDir,
                          Lazy<Dictionary<string, (string Path, string OwningMod)>> archives)
    {
        Language = language; Refusal = refusal; Roots = roots; _dataDir = dataDir; _archives = archives;
    }

    /// <summary>The winning <c>&lt;stem&gt;.bsa</c> across overwrite, enabled mods by priority and the game Data, as the asset layer resolves any archive; null when no layer has one.</summary>
    internal (string Path, string OwningMod)? WinningArchive(string stem)
        => _archives.Value.TryGetValue(stem + ".bsa", out var hit) ? hit : null;

    internal string DataDir => _dataDir;

    internal InstanceText WithRefusal(string refusal) => new(Language, refusal, Roots, _dataDir, _archives);
}

public static class GameLanguage
{
    /// <summary>Every live instance's text, swapped whole under the lock so a lookup never takes it.</summary>
    static InstanceText[] _instances = Array.Empty<InstanceText>();
    static readonly object Gate = new();

    /// <summary>Read the instance's language from the profile's Skyrim.ini, else the game folder's, and check Skyrim.esm has tables in it.</summary>
    public static InstanceText Read(string profileDir, string modsDir, string dataDir, string overwriteDir,
                                    Func<IReadOnlyList<string>> enabledModsByPriority)
    {
        var roots = new[] { modsDir, dataDir, overwriteDir }.Where(r => r.Length > 0).Select(Root).ToArray();
        var archives = new Lazy<Dictionary<string, (string, string)>>(() =>
        {
            // A profile that will not read is refused loud by the order build; here it leaves strings at the game Data, as before #1075.
            try { return ArchiveDiscovery.BuildArchiveMap(enabledModsByPriority(), modsDir, dataDir, overwriteDir); }
            catch (Exception) { return new(StringComparer.OrdinalIgnoreCase); }
        });
        var gamePath = dataDir.Length > 0 ? Path.GetDirectoryName(dataDir.TrimEnd('\\', '/')) ?? "" : "";
        var (raw, ini) = ReadSLanguage(profileDir, gamePath);

        if (raw is null) return new InstanceText(Language.English, null, roots, dataDir, archives);
        if (!raw.All(char.IsLetter) || !Enum.TryParse<Language>(raw, ignoreCase: true, out var language))
            return new InstanceText(Language.English,
                $"houseCARL cannot read this load order: {ini} sets sLanguage={raw}, which is not a game language it "
                + "knows; set it to the language your game is installed in (ENGLISH, GERMAN, FRENCH, RUSSIAN, …).",
                roots, dataDir, archives);

        var text = new InstanceText(language, null, roots, dataDir, archives);
        // Only Skyrim.esm in the game Data is checked: every install has it, and its tables are what the language ships.
        var skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (dataDir.Length == 0 || !File.Exists(skyrim)
            || !HeaderIsLocalized(skyrim) || LocalizedStrings.HasTablesFor(skyrim, text))
            return text;
        return text.WithRefusal(
            $"houseCARL cannot read this load order in {language}: {ini} sets sLanguage={raw}, and no {language} "
            + $"string tables for Skyrim.esm were found in {Path.Combine(dataDir, "Strings")} or in an archive named "
            + "for it; install that language's files or set sLanguage to the language your game is installed in.");
    }

    /// <summary>Make <paramref name="text"/> the answer for every plugin under its roots, replacing the instance it was read for.</summary>
    public static void Use(InstanceText text)
    {
        lock (Gate)
            _instances = _instances.Where(i => !SameRoots(i, text)).Append(text).ToArray();
    }

    /// <summary>Drop an instance's text when its service goes away.</summary>
    public static void Forget(InstanceText? text)
    {
        if (text is null) return;
        lock (Gate) _instances = _instances.Where(i => !ReferenceEquals(i, text)).ToArray();
    }

    /// <summary>The text of the instance whose roots hold <paramref name="pluginPath"/> (the deepest root wins), or null for a file outside every instance.</summary>
    public static InstanceText? For(string pluginPath)
    {
        var all = _instances;
        if (all.Length == 0) return null;
        string full;
        try { full = Path.GetFullPath(pluginPath); } catch { return null; }
        InstanceText? best = null;
        int bestLen = -1;
        foreach (var i in all)
            foreach (var r in i.Roots)
                if (r.Length > bestLen && full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) { best = i; bestLen = r.Length; }
        return best;
    }

    /// <summary>The TES4 header's LOCALIZED bit, read from the raw bytes: this runs before the instance's text is published, so it must not go through the door it is deciding for.</summary>
    static bool HeaderIsLocalized(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> h = stackalloc byte[12];
            return f.Read(h) == 12 && h[..4].SequenceEqual("TES4"u8)
                && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) & 0x80) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool SameRoots(InstanceText a, InstanceText b)
        => a.Roots.SequenceEqual(b.Roots, StringComparer.OrdinalIgnoreCase);

    static string Root(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;

    /// <summary>The raw <c>[General] sLanguage</c> value and the ini it came from; MO2 redirects the game INIs into the profile, so the profile's copy answers first.</summary>
    static (string? Value, string Ini) ReadSLanguage(string profileDir, string gamePath)
    {
        foreach (var dir in new[] { profileDir, gamePath })
        {
            if (dir.Length == 0) continue;
            var ini = Path.Combine(dir, "Skyrim.ini");
            string[] lines;
            try { if (!File.Exists(ini)) continue; lines = File.ReadAllLines(ini); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            bool inGeneral = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[') { inGeneral = line.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inGeneral) continue;
                var eq = line.IndexOf('=');
                if (eq < 0 || !line[..eq].Trim().Equals("sLanguage", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.Length > 0) return (value, ini);
            }
            return (null, ini);   // the first Skyrim.ini there is the one the game reads; no sLanguage in it is English
        }
        return (null, "");
    }
}

namespace HousecarlCore;

/// <summary>Which EditorIDs the game can see for SkyPatcher: without powerofthree's Tweaks it keeps them only for ten record types; contract in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherEditorIds
{
    const string Dll = "SKSE\\Plugins\\po3_Tweaks.dll";
    const string Ini = "SKSE\\Plugins\\po3_Tweaks.ini";

    /// <summary>The Mutagen types whose EditorIDs the game keeps without po3 Tweaks (SkyPatcher's FormID - EditorID article).</summary>
    static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        "Keyword", "Global", "GlobalFloat", "GlobalInt", "GlobalShort", "GlobalBool", "Race", "Quest", "VoiceType",
        "HeadPart", "Cell", "Worldspace", "IdleAnimation", "MusicType",
    };

    /// <summary>Why po3 Tweaks does not load EditorIDs in this view, or null when it does: the DLL must be a loose winner, and a winning po3_Tweaks.ini must not turn [Fixes] bLoadEditorIDs (old name 'Load EditorIDs') off.</summary>
    public static string? Read(AssetResolver.AssetView view)
    {
        var dll = view.ResolveForPlacement(Dll).Sources.FirstOrDefault();
        if (dll is null || dll.Kind != AssetKind.Loose)
            return dll is null
                ? "powerofthree's Tweaks (SKSE\\Plugins\\po3_Tweaks.dll) is not in this order"
                : "powerofthree's Tweaks (SKSE\\Plugins\\po3_Tweaks.dll) is only inside a BSA, where SKSE cannot load it";
        var ini = view.ResolveForPlacement(Ini).Sources.FirstOrDefault();
        if (ini is null || ini.Kind != AssetKind.Loose) return null;   // the DLL writes its defaults, which load EditorIDs
        try
        {
            bool inFixes = false;
            foreach (var raw in File.ReadAllLines(ini.LooseFilePath!))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[') { inFixes = line.TrimEnd().Equals("[Fixes]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (!inFixes || eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Split(';')[0].Trim();
                if ((key.Equals("bLoadEditorIDs", StringComparison.OrdinalIgnoreCase) || key.Equals("Load EditorIDs", StringComparison.OrdinalIgnoreCase))
                    && (val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase)))
                    return $"powerofthree's Tweaks has [Fixes] {key}={val} in its po3_Tweaks.ini (from '{ini.ProviderName}')";
            }
        }
        catch (Exception ex)
        {
            return $"po3_Tweaks.ini (from '{ini.ProviderName}') could not be read ({ex.Message}), so whether Tweaks loads EditorIDs is unknown";
        }
        return null;
    }

    /// <summary>The sentence a warning names when an EditorID of <paramref name="mutagenType"/> cannot be seen, or null when it can.</summary>
    public static string? Gap(string? tweaksMissing, string? mutagenType)
        => tweaksMissing is null || (mutagenType is not null && Kept.Contains(mutagenType)) ? null
            : $"{tweaksMissing}, so the game keeps no EditorIDs of {mutagenType ?? "this"} records";
}

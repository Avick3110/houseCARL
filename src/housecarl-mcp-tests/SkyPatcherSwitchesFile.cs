namespace HousecarlMcpTests;

/// <summary>The one fixture helper that ships a SkyPatcher.ini into a test mod: SkyPatcher 8 runs no patcher but Light
/// whose iEnable…Patching key is missing, so every test world that places SkyPatcher INIs writes the shipped switches.</summary>
static class SkyPatcherSwitchesFile
{
    /// <summary>Write <c>SKSE/Plugins/SkyPatcher.ini</c> under <paramref name="modDir"/> with every documented patcher on, except <paramref name="off"/>.</summary>
    public static void Write(string modDir, params string[] off)
    {
        var dir = Path.Combine(modDir, "SKSE", "Plugins");
        Directory.CreateDirectory(dir);
        var keys = SkyPatcherCatalog.Load().Records.Select(r =>
            $"iEnable{r.Subfolder}Patching={(off.Contains(r.Subfolder, StringComparer.OrdinalIgnoreCase) ? 0 : 1)}");
        File.WriteAllText(Path.Combine(dir, "SkyPatcher.ini"), "[Patcher]\r\n" + string.Join("\r\n", keys) + "\r\n");
    }

    /// <summary>Place <c>SKSE/Plugins/po3_Tweaks.dll</c> (empty: only its presence is read) under <paramref name="modDir"/>, so the game keeps EditorIDs of every type and a line may name any record by EditorID.</summary>
    public static void WriteTweaks(string modDir)
    {
        var dir = Path.Combine(modDir, "SKSE", "Plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "po3_Tweaks.dll"), Array.Empty<byte>());
    }
}

namespace HousecarlCore;

/// <summary>Fold one not-yet-placed SkyPatcher INI into the discovered layer, so a record reads as the game would see it once the draft is placed; contract in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherDraft
{
    /// <summary>A validated draft: the file, the type folder it would sit in, and whether that folder was taken from the file's parent directory rather than named.</summary>
    public sealed record Plan(string IniPath, string Subfolder, bool SubfolderInferred)
    {
        /// <summary>The arm clause a response leads with, so the answer always names the draft it included.</summary>
        public string Arm => $"the draft INI '{IniPath}' placed in the '{Subfolder}' folder"
                           + (SubfolderInferred ? " (subfolder taken from the draft's parent directory)" : "");

        /// <summary>Fold this draft into a live layer scan, sorted among the live files as <see cref="SkyPatcherDiscovery.Scan"/> sorts them; returns the live scan unchanged when <paramref name="refusal"/> is set, and <paramref name="warnings"/> collects what a replay would otherwise swallow.</summary>
        public SkyPatcherDiscovery.LayerScan Fold(SkyPatcherDiscovery.LayerScan live, SkyPatcherCatalog catalog,
                                                  Func<string, bool> pluginPresent, out string? refusal,
                                                  SkyPatcherOverlay.WarningSink? warnings = null)
        {
            refusal = null;
            var name = Path.GetFileName(IniPath);
            int at = -1;
            for (int i = 0; i < live.Folders.Count; i++)
                if (live.Folders[i].Subfolder.Equals(Subfolder, StringComparison.OrdinalIgnoreCase)) { at = i; break; }

            // A draft that IS one of the layer's live files would replay its lines twice; the filename clash below catches only a live copy sitting flat in the type folder.
            var placed = live.Folders.SelectMany(f => f.Files)
                             .FirstOrDefault(f => f.LooseFilePath is { } p && SamePath(p, IniPath));
            if (placed is not null)
            {
                refusal = $"the draft '{IniPath}' is already placed — it is the file the layer reads as '{placed.RelPath}'"
                        + (placed.WinningProvider is null ? "" : $" (from '{placed.WinningProvider}')")
                        + ", so folding it in would replay its lines twice; read it with the plain post state by dropping \"ini\".";
                return live;
            }

            // Same relative path once placed is the VFS same-path collision: one copy wins by mod order and the other is never read.
            var clash = at < 0 ? null
                : live.Folders[at].Files.FirstOrDefault(f => f.SortKey.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                refusal = $"the draft's filename '{name}' is already live in the '{Subfolder}' folder as '{clash.RelPath}'"
                        + (clash.WinningProvider is null ? "" : $" (from '{clash.WinningProvider}')")
                        + ", so placing the draft there would shadow that file or be shadowed by it depending on mod order — give the draft a filename not yet placed in that folder, which is also what decides where it sorts among them.";
                return live;
            }

            IReadOnlyList<SkyPatcherLine> lines;
            try { lines = SkyPatcherParse.ParseFile(File.ReadAllText(IniPath)); }
            catch (Exception ex)
            {
                refusal = $"the draft '{IniPath}' could not be read: {ex.Message}";
                return live;
            }

            // The filename gate applies to the draft as to any file.
            var gate = SkyPatcherDiscovery.GatePluginOf(name);
            string? notApplied = null;
            if (gate is not null && !pluginPresent(gate))
            {
                notApplied = $"filename-gated on plugin '{gate}', which is not in the active load order";
                warnings?.Add($"{IniPath}: the draft is {notApplied} — SkyPatcher would not read it once placed, so this post state is the plain winner.");
            }

            var draft = new SkyPatcherDiscovery.IniFile(IniPath, Subfolder, name, DraftProvider, IniPath,
                                                        Array.Empty<string>(), gate, notApplied, lines);

            // The [Patcher] toggle for this type; a folder invented here has no scan to carry it, so it is read off the layer's toggle map.
            bool folderEnabled = at >= 0
                ? live.Folders[at].PatchingEnabled
                : SkyPatcherDiscovery.ToggleEnabled(live.PatcherToggles, Subfolder);
            if (!folderEnabled)
                warnings?.Add($"{IniPath}: SkyPatcher.ini disables '{Subfolder}' patching (iEnable…Patching=0) — the DLL skips the whole folder, " +
                              "so the draft would apply nothing once placed and this post state is the plain winner.");

            var folders = new List<SkyPatcherDiscovery.FolderScan>(live.Folders);
            if (at < 0)
            {
                folders.Add(new SkyPatcherDiscovery.FolderScan(Subfolder, catalog.ForSubfolder(Subfolder), folderEnabled,
                                                               new List<SkyPatcherDiscovery.IniFile> { draft }));
                folders = folders.OrderBy(f => f.Subfolder, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                var files = folders[at].Files.Append(draft)
                                             .OrderBy(f => f.SortKey, StringComparer.OrdinalIgnoreCase).ToList();
                folders[at] = folders[at] with { Files = files };
            }

            var notes = live.Notes.Append(
                $"the draft INI '{IniPath}' was folded into the '{Subfolder}' folder as '{name}' — it is NOT on disk in a mod, " +
                "so this reads what the game would see once it is placed there.").ToList();
            return new SkyPatcherDiscovery.LayerScan(folders, notes, live.ReadIncomplete, live.PatcherToggles);
        }
    }

    /// <summary>Two paths naming the same file, compared as the filesystem does here: normalized, case-insensitive.</summary>
    static bool SamePath(string a, string b)
    {
        try { return Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>What the draft's provider column says: it comes from no mod, so a render cannot name a mod folder that does not have it.</summary>
    public const string DraftProvider = "the draft (not placed in a mod)";

    /// <summary>Validate a draft pole expression into a <see cref="Plan"/>, returning the refusal sentence or null; a null <paramref name="subfolder"/> falls back to the file's parent directory name when that is a documented type folder.</summary>
    public static string? Prepare(string? iniPath, string? subfolder, SkyPatcherCatalog catalog, out Plan? plan)
    {
        plan = null;
        var path = (iniPath ?? "").Trim();
        if (path.Length == 0)
            return "the draft INI path is empty — give the absolute path to the .ini file the draft would be placed as.";
        if (PathArguments.NotAbsolute(path, "the draft INI path", "the .ini file the draft would be placed as",
                                      "C:\\MO2\\mods\\MyPatch\\SkyPatcher\\weapon\\MyPatch.ini") is { } notAbsolute)
            return notAbsolute;
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) { return $"the draft INI path '{path}' is not a usable path: {ex.Message}"; }
        if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            return $"the draft '{path}' is not a .ini file — SkyPatcher reads only .ini files from its type folders.";
        if (!File.Exists(path))
            return $"there is no file at '{path}' — the draft is read from disk, so it has to exist before it can be checked.";

        var asked = subfolder?.Trim();
        bool inferred = string.IsNullOrEmpty(asked);
        if (inferred)
        {
            asked = Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            if (catalog.ForSubfolder(asked) is null)
                return $"the SkyPatcher type folder for the draft was taken from its parent directory '{asked}', which is not a documented SkyPatcher record type " +
                       $"(not in the grammar reference; verify the folder name or the reference version) — name the folder with \"subfolder\". The documented folders are: {Documented(catalog)}.";
        }
        else if (catalog.ForSubfolder(asked!) is null)
            return $"subfolder '{asked}' is not a documented SkyPatcher record type (not in the grammar reference; verify the folder name or the reference version). " +
                   $"The documented folders are: {Documented(catalog)}.";

        // The catalog's own spelling, so a draft folder matches a live one case for case.
        plan = new Plan(path, catalog.ForSubfolder(asked!)!.Subfolder, inferred);
        return null;
    }

    static string Documented(SkyPatcherCatalog catalog)
        => string.Join(", ", catalog.Records.Select(r => r.Subfolder).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
}

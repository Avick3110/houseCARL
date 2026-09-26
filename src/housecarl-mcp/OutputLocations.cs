using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

// The output folders (rider, patch and the .seq writer), owned-folder resolution and plugin-locate-on-disk; contract in docs/architecture/output-and-artifacts.md.
public sealed partial class LoadOrderService
{
    /// <summary>The resolved output location for a NON-.esp rider: the directory to WRITE into, the mod-folder ROOT cleanup operates on, whether THIS call created it fresh, and the folder's <paramref name="Stem"/> without the "houseCARL - " prefix; contract in docs/architecture/output-and-artifacts.md.</summary>
    public readonly record struct RiderFolder(string OutputDir, string ModFolder, bool CreatedFresh, string Stem);

    /// <summary>How ONE rider lane names the mod folder it creates — the calling tool's own statement, the way <see cref="FreshPatchRemedy"/> is for the record lanes. <paramref name="RefuseTaken"/> is set only by a lane whose artifact's exact basename is load-bearing.</summary>
    public readonly record struct RiderNaming(string Param, StemRefusal? RefuseTaken = null);

    /// <summary>Resolve a houseCARL-owned mod folder under ModsDir for a non-.esp output: a fresh marker-stamped folder, auto-suffixed so a prior one is never clobbered, or <paramref name="into"/> an existing owned one. Derives ModsDir with no index build, and throws the unconfigured prompt when there is no instance.</summary>
    public RiderFolder ResolvePatchModFolder(string? patchName, string? into, string defaultStem, RiderNaming? naming)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir from the instance, NO resolver build
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                // needEsp:false because a rider targets the FOLDER, and the fresh remedy is the CALLING LANE's (#357).
                var folder = ResolveOwnedPatchFolder(into, needEsp: false, FreshPatchRemedy.None, riderNaming: naming);
                return new RiderFolder(folder, folder, CreatedFresh: false, FolderStem(folder));   // reused — the user owns it; cleanup leaves it
            }

            var newStem = UniqueStem(PatchStem(string.IsNullOrWhiteSpace(patchName) ? defaultStem : patchName!),
                                     !string.IsNullOrWhiteSpace(patchName), writes: null, naming?.RefuseTaken);
            var newFolder = Path.Combine(_modsDir, ModFolderName(newStem));
            Directory.CreateDirectory(newFolder);
            WriteOwnerMeta(newFolder, "(houseCARL output)");   // ownership marker; this folder may hold scripts / a .bsa / loose files, not an .esp
            return new RiderFolder(newFolder, newFolder, CreatedFresh: true, newStem);
        }
    }

    /// <summary>The <c>Scripts\</c> output folder for a compiled .pex, under a houseCARL mod folder, which MO2 deploys into the game's Data\Scripts.</summary>
    public RiderFolder ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
        var scripts = Path.Combine(f.ModFolder, "Scripts");
        Directory.CreateDirectory(scripts);
        return f with { OutputDir = scripts };
    }

    /// <summary>The out_path= lane for a compiled .pex: the caller names a mod-folder ROOT and houseCARL appends <c>Scripts\</c>; <paramref name="deployWarning"/> is non-null when that path is one the game will not auto-load from. Contract in docs/architecture/output-and-artifacts.md.</summary>
    public RiderFolder ResolveExplicitScriptFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "Scripts", ScriptOutputContract, out deployWarning);

    /// <summary>The same out_path= contract for the SEQ rider, with <c>SEQ\</c> appended; it exists because the in-place .esp lane leaves the .esp in the mod's own folder, which the default .seq output model does not serve.</summary>
    public RiderFolder ResolveExplicitSeqFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "SEQ", SeqOutputContract, out deployWarning);

    /// <summary>The shared body of the out_path= lanes: refuse an unusable path, normalize the root, apply <paramref name="contract"/>, create the folder, and hand back a user-owned RiderFolder. One body, so the rules cannot drift per artifact.</summary>
    RiderFolder ResolveExplicitRiderFolder(
        string outputDir, string sub,
        Func<string, string, string, string, (string dir, bool appended, string? deployWarning)> contract,
        out string? deployWarning)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir/DataDir for the deployability check, NO resolver build
            var given = (outputDir ?? "").Trim().Trim('"');
            if (PathArguments.NotAbsolute(given, "out_path", $"the mod-folder root to write into (houseCARL appends {sub}\\)",
                                          "C:\\MO2\\mods\\MyMod") is { } notAbsolute)
                throw new InvalidOperationException(notAbsolute);
            string root;
            try { root = Path.GetFullPath(given); }
            catch (Exception ex) { throw new InvalidOperationException($"out_path '{outputDir}' is not a usable path ({ex.Message})."); }
            if (File.Exists(root))
                throw new InvalidOperationException($"out_path '{root}' is a file, not a folder. Give a mod-folder root — houseCARL appends {sub}\\.");

            var (outDir, appended, warn) = contract(root, _modsDir, _dataDir, _overwriteDir);
            // A plain message for a folder that cannot be created, rather than a generic internal failure.
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"out_path: couldn't create the output folder '{outDir}' ({ex.Message}). Check the path and that it's writable."); }
            deployWarning = warn;
            // ModFolder stays accurate though cleanup is bypassed: the subfolder's parent, else the path given.
            var modRoot = appended ? root : (Path.GetDirectoryName(outDir.TrimEnd('\\', '/')) ?? outDir);
            return new RiderFolder(outDir, modRoot, CreatedFresh: false, FolderStem(modRoot));   // user-owned: residue cleanup never touches it
        }
    }

    /// <summary>Pure, filesystem-free resolution of the out_path= contract for <c>Scripts\</c>, so it is testable without an MO2 instance; returns the final dir, whether the segment was appended, and the deployWarning.</summary>
    internal static (string scriptsDir, bool appendedScripts, string? deployWarning) ScriptOutputContract(
        string outputDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var (scriptsDir, appended, deployable) = SubfolderOutputContract(outputDir, "Scripts", modsDir, dataDir, overwriteDir);
        string? warn = deployable ? null :
            $"note: '{scriptsDir}' isn't a folder MO2 (or the game) auto-loads scripts from, so the compiled .pex won't " +
            "deploy on its own — it compiled fine, but you must place it where the game loads scripts yourself: a mod's " +
            "own Scripts\\ folder (<mods>\\<YourMod>\\Scripts), the MO2 overwrite folder, or the game's <Data>\\Scripts.";
        return (scriptsDir, appended, warn);
    }

    /// <summary><see cref="ScriptOutputContract"/>'s twin for the <c>.seq</c>: the same pure path contract with <c>SEQ\</c> appended, and a warning worded for what a mis-placed .seq costs.</summary>
    internal static (string seqDir, bool appendedSeq, string? deployWarning) SeqOutputContract(
        string outputDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var (seqDir, appended, deployable) = SubfolderOutputContract(outputDir, "SEQ", modsDir, dataDir, overwriteDir);
        string? warn = deployable ? null :
            $"note: '{seqDir}' isn't a folder MO2 (or the game) reads SEQ files from, so the game will NOT see this .seq — " +
            "the file is correct, but until it sits somewhere loaded the plugin's start-game-enabled quests stay silently " +
            "dead. Put it in a mod's own SEQ\\ folder (<mods>\\<YourMod>\\SEQ — enabled in MO2), the MO2 overwrite folder, or the game's <Data>\\SEQ.";
        return (seqDir, appended, warn);
    }

    /// <summary>The shared pure core of the out_path= contracts: append <paramref name="sub"/> to a mod-folder root, never doubling a segment already there, and decide deployability. The deploy shape rule is in docs/architecture/output-and-artifacts.md; the per-artifact sentence stays with each caller.</summary>
    static (string dir, bool appendedSub, bool deployable) SubfolderOutputContract(
        string outputDir, string sub, string modsDir, string dataDir, string overwriteDir = "")
    {
        // A drive root keeps its separator: trimming "C:\" gives the drive-RELATIVE "C:", which resolves elsewhere.
        var root = IsRoot(outputDir) ? outputDir : outputDir.TrimEnd('\\', '/');
        bool already = Path.GetFileName(root).Equals(sub, StringComparison.OrdinalIgnoreCase);
        var dir = already ? root : Path.Combine(root, sub);
        return (dir, !already,
            IsModDeployFolder(dir, modsDir) || IsDataDeployFolder(dir, dataDir) || IsDataDeployFolder(dir, overwriteDir));
    }

    /// <summary>Is this path a filesystem root whose trailing separator is part of its meaning — <c>C:\</c>, where trimming yields the drive-relative <c>C:</c>?</summary>
    static bool IsRoot(string path)
    {
        try { return string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>A deploy folder MO2 actually serves: exactly <c>&lt;modsDir&gt;\&lt;modFolder&gt;\&lt;sub&gt;</c>. The rule is about shape, not the segment's name, so every lane shares it; an empty mods root is false.</summary>
    static bool IsModDeployFolder(string outDir, string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return false;
        var modFolder = Path.GetDirectoryName(outDir.TrimEnd('\\', '/'));       // expect <mods>\<modFolder>
        return modFolder is not null && PathEquals(Path.GetDirectoryName(modFolder), modsDir);
    }

    /// <summary>A direct game install loads exactly <c>&lt;dataDir&gt;\&lt;sub&gt;</c>, never Data\Sub\…; an empty data dir is false.</summary>
    static bool IsDataDeployFolder(string outDir, string dataDir)
    {
        if (string.IsNullOrEmpty(dataDir)) return false;
        return PathEquals(Path.GetDirectoryName(outDir.TrimEnd('\\', '/')), dataDir);
    }

    /// <summary>Case-insensitive equality of two paths after normalization and a trailing-separator trim, with no filesystem access; a null left side is never equal.</summary>
    static bool PathEquals(string? a, string b)
    {
        if (a is null) return false;
        return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>Source\Scripts\</c> output folder for a decompiled .psc — the SE-canonical layout, under the same default patch stem as the compile lane so decompile, edit and compile accumulate in one folder.</summary>
    public RiderFolder ResolveDecompiledSourceFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
        var src = Path.Combine(f.ModFolder, "Source", "Scripts");
        Directory.CreateDirectory(src);
        return f with { OutputDir = src };
    }

    /// <summary>The <c>out_path=</c> lane for a decompiled .psc: the caller names a folder and the .psc lands straight in it, nothing appended, since a .psc is source a compiler reads rather than a file the game loads, so there is no deployability question. Reads no instance state, takes no lock, and runs with no instance configured.</summary>
    public static RiderFolder ResolveExplicitSourceFolder(string outPath)
    {
        var given = (outPath ?? "").Trim().Trim('"');
        if (PathArguments.NotAbsolute(given, "out_path", "the folder the .psc should land in", "C:\\work\\sources") is { } notAbsolute)
            throw new InvalidOperationException(notAbsolute);
        string root;
        try { root = Path.GetFullPath(given); }
        catch (Exception ex) { throw new InvalidOperationException($"out_path '{outPath}' is not a usable path ({ex.Message})."); }
        if (File.Exists(root))
            throw new InvalidOperationException($"out_path '{root}' is a file, not a folder. Give the folder the .psc should land in.");
        try { Directory.CreateDirectory(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException($"out_path: couldn't create the output folder '{root}' ({ex.Message}). Check the path and that it's writable."); }
        return new RiderFolder(root, root, CreatedFresh: false, FolderStem(root));   // caller-owned: cleanup never touches it
    }

    /// <summary>Clean up after a rider that failed having cut a fresh folder: a folder holding nothing but our own meta.ini is deleted, one holding real output is kept and its path returned to name, and a reused into= folder is never touched. Best-effort, and never masks the rider's own outcome.</summary>
    internal string? RemoveOrNameRiderResidue(RiderFolder folder)
    {
        if (!folder.CreatedFresh) return null;             // into= reuse — the user owns it, never deleted or named
        var root = folder.ModFolder;
        try
        {
            if (!Directory.Exists(root)) return null;
            bool onlyMarker = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .All(f => Path.GetFileName(f).Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
            if (onlyMarker) { Directory.Delete(root, recursive: true); return null; }   // genuinely empty → gone, nothing to name
            return root;                                   // real output landed → keep it, hand back the path to NAME
        }
        catch (IOException) { return Directory.Exists(root) ? root : null; }
        catch (UnauthorizedAccessException) { return Directory.Exists(root) ? root : null; }
    }

    // ---- write the start-game-enabled-quest .seq file ----

    /// <summary>The <c>SEQ\</c> output folder for a generated <c>.seq</c>, under a houseCARL mod folder, which MO2 deploys into the game's <c>Data\SEQ</c>.</summary>
    public RiderFolder ResolveSeqFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_SEQ", new RiderNaming("patch"));
        var seq = Path.Combine(f.ModFolder, "SEQ");
        Directory.CreateDirectory(seq);
        return f with { OutputDir = seq };
    }

    /// <summary>The patch stem of the houseCARL folder <paramref name="pluginPath"/> lives in, so the <c>.seq</c> defaults beside the <c>.esp</c>; only when that folder is the canonical one for this plugin, so a later <c>into=</c> resolves to exactly it, else null.</summary>
    string? OwnedPluginFolderStem(string pluginPath)
    {
        var dir = Path.GetDirectoryName(pluginPath);
        if (dir is null || Path.GetDirectoryName(dir) is not { } parent || !PathEquals(parent, _modsDir)) return null;
        if (!IsHouseCarlOwned(dir)) return null;
        var stem = PatchStem(Path.GetFileName(pluginPath));
        return Path.GetFileName(dir).Equals(ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? stem : null;
    }

    /// <summary>Write a plugin's start-game-enabled-quest <c>.seq</c> — the file the engine reads to actually start those quests — into <c>&lt;ModFolder&gt;\SEQ\</c>, defaulting to the plugin's own houseCARL folder when it lives in one. Serialized on the write gate.
    /// <para><paramref name="outputDir"/> is the out_path= contract, and wins over <paramref name="patchName"/> and <paramref name="into"/>. A plugin with no such quests writes nothing and cuts no folder, and a destination already holding exactly these bytes is reported as such rather than rewritten; both are stated explicitly, since an unstated skip reads as a silent failure.</para></summary>
    public SeqOutcome WriteSeq(string plugin, string? patchName, string? into, string? outputDir = null)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return SeqOutcome.Fail("no source given. Pass source= the plugin whose start-game-enabled quests need a .seq — its filename (e.g. 'MyQuestMod.esp') or an absolute path.");
        plugin = plugin.Trim().Trim('"');

        // Source resolution through the shared locate contract; the arm that resolved decides which .seq you get.
        string pluginPath, resolvedFrom;
        try
        {
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            var comp = Mo2LoadOrder.ReadComposition(profileDir);        // cheap text parse — no index build
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, null, offerModParam: false);
            // The locate refusal names what it could not find; this adds what THIS tool accepts.
            if (loc.Error is not null)
                return SeqOutcome.Fail($"{loc.Error} Pass source= the plugin's FILENAME (located across your MO2 mod folders, the overwrite folder and game Data) or an ABSOLUTE path to the .esp/.esm/.esl.");
            if (loc.Ambiguous is { } hits)
                return SeqOutcome.Fail($"'{Path.GetFileName(plugin)}' is provided by {hits.Count} locations — name the one you mean by absolute path: "
                                     + string.Join("; ", hits.Select(h => $"{h.Where} -> {h.Path}")));
            pluginPath = loc.Path!;
            resolvedFrom = loc.Where;
        }
        catch (Exception ex) { return SeqOutcome.Fail(ex.Message); }

        if (!PluginExts.Contains(Path.GetExtension(pluginPath), StringComparer.OrdinalIgnoreCase))
            return SeqOutcome.Fail($"'{Path.GetFileName(pluginPath)}' is not a plugin (.esp/.esm/.esl).");

        // Lock order is _writeGate then _gate; contract in docs/architecture/load-order-service.md.
        lock (_writeGate)                                                // one write at a time: build, resolve, commit
        {
            if (ConfigPromptOrNull() is { } cfgPrompt) return SeqOutcome.Fail(cfgPrompt);   // need ModsDir for the output folder
            lock (_gate) EnsurePathsDerived();                          // derive ModsDir for the owned-folder check

            // Build the .seq from the plugin: a read-only overlay, disposed inside, so no handle is held at rest.
            SeqFile.SeqBuild built;
            try { built = SeqFile.Build(pluginPath); }
            catch (Exception ex)
            { return SeqOutcome.Fail($"could not read '{Path.GetFileName(pluginPath)}' as a plugin: {ex.Message}"); }

            // No SGE quests: write nothing, cut no folder, and still carry UserChoseOutput, a fact about the CALL.
            if (built.Quests.Count == 0)
                return new SeqOutcome(true, null, null, null, built.Quests, built.PluginFileName, false)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, UserChoseOutput = !string.IsNullOrWhiteSpace(outputDir) };

            // Output folder: out_path= wins, else the plugin's own houseCARL folder, else a fresh one or into=/patch.
            bool chosenOutput = !string.IsNullOrWhiteSpace(outputDir);
            string? autoInto = (!chosenOutput && string.IsNullOrWhiteSpace(into) && string.IsNullOrWhiteSpace(patchName))
                ? OwnedPluginFolderStem(pluginPath) : null;
            RiderFolder rf;
            string? deployWarning = null;
            try
            {
                rf = chosenOutput
                    ? ResolveExplicitSeqFolder(outputDir!, out deployWarning)
                    : ResolveSeqFolder(patchName, autoInto ?? into);
            }
            catch (InvalidOperationException ex) { return SeqOutcome.Fail(ex.Message); }

            var seqName = Path.GetFileNameWithoutExtension(pluginPath) + ".seq";
            var dest = Path.Combine(rf.OutputDir, seqName);

            // Identical bytes are reported, not rewritten, but an identical file OLDER than the plugin is stamped
            // forward for the mtime-based staleness test, and a failed touch falls THROUGH to the real write.
            bool sameBytes = SameBytesOnDisk(dest, built.Bytes), identical = sameBytes, touched = false;
            if (identical && !RefreshSeqTimestamp(dest, pluginPath, out touched)) identical = false;
            if (identical)
                return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, Unchanged = true, TimestampRefreshed = touched,
                      UserChoseOutput = chosenOutput, DeployWarning = deployWarning };

            // On out_path= the replaced file may be the mod's own .seq, and an identical replace lost nothing.
            bool replaced = File.Exists(dest);
            bool replacedSameBytes = replaced && sameBytes;

            // Atomic write of <plugin>.seq under SEQ\.
            try { AtomicFile.WriteAllBytes(dest, built.Bytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);             // nothing landed → a fresh folder is an orphan
                return SeqOutcome.Fail($"could not write '{seqName}': {ex.Message}"
                    + (residue is null ? "" : $" The freshly created folder was left at '{residue}'.")
                    // out_path bypasses cleanup, so its SEQ\ folder is still there, and this call may not have cut it.
                    + (chosenOutput ? $" (the '{rf.OutputDir}' folder is left in place — houseCARL never removes a folder you named.)" : ""));
            }

            // Integrity: the on-disk size matches the bytes built, so success is never claimed falsely.
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != built.Bytes.Length)
                return SeqOutcome.Fail($"wrote '{seqName}' but its on-disk size ({size}) does not match the {built.Bytes.Length} expected byte(s) — verify before relying on it.");

            return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null)
                { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, Replaced = replaced,
                  ReplacedSameBytes = replacedSameBytes,
                  UserChoseOutput = chosenOutput, DeployWarning = deployWarning };
        }
    }

    /// <summary>Keep a skipped write honest against the mtime-based .seq staleness test by stamping a byte-identical but older file forward; <paramref name="touched"/> says whether a stamp was needed, and false means it was needed and failed, so the caller does the real write.</summary>
    static bool RefreshSeqTimestamp(string seqPath, string pluginPath, out bool touched)
    {
        touched = false;
        try
        {
            var seqTime = File.GetLastWriteTimeUtc(seqPath);
            var pluginTime = File.GetLastWriteTimeUtc(pluginPath);
            if (seqTime >= pluginTime) return true;                 // already newer — the staleness test is satisfied
            // Now is not enough against a plugin stamped in the FUTURE, so stamp past the plugin and then verify.
            var target = pluginTime > DateTime.UtcNow ? pluginTime.AddSeconds(1) : DateTime.UtcNow;
            File.SetLastWriteTimeUtc(seqPath, target);
            if (File.GetLastWriteTimeUtc(seqPath) < File.GetLastWriteTimeUtc(pluginPath)) return false;
            touched = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Does <paramref name="path"/> already hold exactly <paramref name="bytes"/>? Any IO problem answers false, because a wrong true here leaves a stale .seq reported as current.</summary>
    static bool SameBytesOnDisk(string path, byte[] bytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != bytes.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch { return false; }
    }

    /// <summary>The MO2 mod-folder name for a patch stem; the prefix is the human-visible ownership signal, while the meta.ini marker is the structural one.</summary>
    static string ModFolderName(string stem) => "houseCARL - " + stem;

    /// <summary>The stem a mod folder carries: the inverse of <see cref="ModFolderName"/> where one applies, else the folder's own name.</summary>
    static string FolderStem(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string prefix = "houseCARL - ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

    /// <summary>Plugin extensions stripped from a caller-supplied patch name, aliasing the one shared home so this and the load-order reader cannot diverge.</summary>
    static readonly string[] PluginExts = PluginFile.Extensions;

    /// <summary>Reduce a caller name to a safe bare STEM: no directory parts, so it cannot escape ModsDir, and ONLY a trailing plugin extension stripped, not every dot, so a dotted patch name survives an <c>into=</c> round trip intact.</summary>
    static string PatchStem(string raw)
    {
        var name = Path.GetFileName(raw.Trim());
        foreach (var ext in PluginExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        return string.IsNullOrEmpty(name) ? "Patch" : name;
    }

    /// <summary>The given stem if it is free, else the first free "<c>&lt;stem&gt;_NNN</c>"; free means no mod folder of that name exists AND no active plugin is named "<c>&lt;stem&gt;.esp</c>". Auto-suffix rule and its two refusing lanes in docs/architecture/output-and-artifacts.md.
    /// <para><paramref name="writes"/> is the calling lane's own statement of the file it emits and the parameter that names it, so a shadow refusal never sends a caller to a parameter their tool lacks. Both refusals need <paramref name="stemFromCaller"/>: a shadow, and a taken stem under <paramref name="refuseTaken"/>, refuse only the name the CALLER passed, while a defaulted stem is suffixed either way.</para></summary>
    string UniqueStem(string stem, bool stemFromCaller, PatchStemShadow.Target? writes, StemRefusal? refuseTaken = null)
    {
        var active = ActivePluginBasenames();
        // The shadow sweep needs to know what the order loads, so without that it does not run and folder plus
        // active-order uniqueness stand; a lane that writes no plugin does not pay the profile parse at all.
        var comp = active.Count == 0 || writes is null ? null : ReadCompositionForShadow();
        if (Takeable(stem)) return stem;
        for (int i = 1; i < 10000; i++)
        {
            var cand = $"{stem}_{i:D3}";
            if (Takeable(cand)) return cand;
        }
        throw new InvalidOperationException($"too many patches named '{stem}' under ModsDir — clean some out.");

        // Free of a folder and an active plugin, and shadowing nothing; only a shadow on the CALLER's name refuses.
        bool Takeable(string s)
        {
            if (StemCollision(s, active) is { } taken)
            {
                if (refuseTaken is { } r && stemFromCaller && s == stem)
                    throw new InvalidOperationException(
                        $"{taken} — houseCARL won't auto-rename {r.Artifact}, whose exact basename is load-bearing, so " +
                        $"nothing was written. {r.Remedy}");
                return false;
            }
            if (comp is null || writes is not { } w) return true;
            var file = w.PluginFor(s);
            if (PatchStemShadow.Find(comp, _modsDir, _dataDir, _overwriteDir, file, active) is not { } hit) return true;
            if (stemFromCaller && s == stem)
                throw new InvalidOperationException(PatchStemShadow.Refusal(file, hit, w.Param));
            return false;
        }
    }

    /// <summary>The profile composition the shadow sweep walks, or null when the profile cannot tell a shadow from a loaded plugin. The test is whether the composition is USABLE, not whether the read threw: a missing modlist.txt returns empty mod lists, under which every folder reads as unlisted and a genuinely loaded plugin would be refused. A PARTIAL modlist.txt is not detectable here and is not claimed to be.</summary>
    Mo2Composition? ReadCompositionForShadow()
    {
        try
        {
            var comp = Mo2LoadOrder.ReadComposition(_profileDir);
            // No mod named in either list is an unusable profile.
            return comp.EnabledMods.Count == 0 && comp.DisabledMods.Count == 0 ? null : comp;
        }
        catch { return null; }
    }

    /// <summary>Null when a stem is free to claim, else the sentence naming WHICH of the two tests is in the way, so a refusing lane can say it.</summary>
    string? StemCollision(string stem, IReadOnlySet<string> activePlugins)
        => Directory.Exists(Path.Combine(_modsDir, ModFolderName(stem)))
            ? $"a mod folder '{ModFolderName(stem)}' already exists"
            : activePlugins.Contains(stem + ".esp")
                ? $"a plugin named '{stem}.esp' is already active in your load order"
                : null;

    /// <summary>One lane's statement that its artifact's exact basename is load-bearing, so a taken stem the CALLER named refuses instead of auto-suffixing, naming the artifact and that lane's own remedy.</summary>
    public readonly record struct StemRefusal(string Artifact, string Remedy);

    /// <summary>The active load order's plugin filenames for the UniqueStem collision check, read from the built resolver if present else the cheap composition — deliberately not via the <see cref="Resolver"/> getter, which refuses a zero-plugin instance. Best-effort: an empty set leaves folder-only uniqueness.</summary>
    IReadOnlySet<string> ActivePluginBasenames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            IReadOnlyList<string>? names = _resolver?.PluginNames;
            if (names is null)
                names = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir)
                    .OrderedPaths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToList()!;
            foreach (var n in names) set.Add(n);
        }
        catch { /* unreadable or empty load order → folder-only uniqueness */ }
        return set;
    }

    /// <summary>The four-step <c>into=</c> extend resolver, shared by the .esp write path and the rider and asset path so "extend my renamed patch" behaves identically everywhere; the arms and their ownership gate are in docs/architecture/output-and-artifacts.md. <paramref name="needEsp"/> tightens the canonical arm for the record lane. Caller holds <see cref="_gate"/>.
    /// <para><paramref name="freshPatch"/> is the calling operation's own statement of how it can create a patch, and <paramref name="noFreshRule"/> the same statement from a lane the enum cannot express, saying WHY there is no fresh route; each refusal is ONE sentence with the nearest owned patches named inside it (#359, #380).</para></summary>
    string ResolveOwnedPatchFolder(string into, bool needEsp,
                                   FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                                   RiderNaming? riderNaming = null)
    {
        var stem = PatchStem(into);                             // strips a trailing .esp/.esm/.esl; no directory parts (can't escape ModsDir)
        var espName = stem + ".esp";

        // Canonical fast path, with no scan; the record lane also requires the folder to hold <stem>.esp.
        var canonical = Path.Combine(_modsDir, ModFolderName(stem));
        if (Directory.Exists(canonical) && IsHouseCarlOwned(canonical) && (!needEsp || File.Exists(Path.Combine(canonical, espName))))
            return canonical;

        // By plugin name: the owned folder holding <stem>.esp, whatever it is now called — the renamed-folder case.
        var byEsp = OwnedFoldersHolding(espName)
            .Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (byEsp.Count == 1) return byEsp[0];
        // Ambiguous: refuse and name every candidate folder as a ready-to-paste into= value.
        if (byEsp.Count > 1)
            throw new InvalidOperationException(
                $"cannot extend: {byEsp.Count} houseCARL folders carry '{espName}' — ambiguous, refusing to guess. " +
                "Pass the CONTAINING mod-folder name as into= to pick one (folder & plugin names need not match): " +
                string.Join("  |  ", byEsp.Select(d => $"into=\"{Path.GetFileName(d)}\"")) + ".");

        // Folder catch-all: into= names the mod folder itself, the same-named-plugin disambiguator.
        var named = ResolveOwnedFolderByName(into);
        if (named is not null) return named;

        // Nothing matched: a foreign un-owned collision and a genuine miss get different refusals.
        var bareName = Path.GetFileName(into.Trim());
        foreach (var cand in new[] { ModFolderName(stem), bareName })
        {
            var candPath = string.IsNullOrEmpty(cand) ? null : Path.Combine(_modsDir, cand);
            if (candPath is not null && Directory.Exists(candPath) && !IsHouseCarlOwned(candPath))
                // No fresh stem is handed back — two same-named plugins cannot both be active (#359) — and the
                // in-place lane is not offered here, staying on the tool that declares it (Aaron, 2026-09-05).
                throw new InvalidOperationException(ExtendRefusal(
                    $"mod folder '{cand}' exists but was NOT created by houseCARL (no marker), so writing into it "
                    + "would touch a mod houseCARL doesn't own (originals untouched, Q3)",
                    noFreshRule,
                    OwnedPatchCandidates(needEsp, stem),
                    riderNaming is { } fr
                        ? $"dropping into= and passing {fr.Param}= a name no mod folder already uses for a fresh folder"
                        : freshPatch switch
                        {
                            FreshPatchRemedy.NamedByPatchParam => "dropping into= and passing patch= a name no mod folder already uses for a fresh patch",
                            FreshPatchRemedy.CreatedByOmittingInto => "omitting into= for a fresh patch",
                            _ => null,
                        }));
        }
        // The fresh clause is the caller's own, and says to DROP into=, which every lane extends on (#357).
        throw new InvalidOperationException(ExtendRefusal(
            $"no houseCARL patch named '{stem}' — no owned folder holds '{espName}' and none is named "
            + $"'{ModFolderName(stem)}'"
            + (string.Equals(bareName, ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? "" : $" or '{bareName}'"),
            noFreshRule,
            OwnedPatchCandidates(needEsp, stem),
            riderNaming is { } rn
                ? $"dropping into= and passing {rn.Param}=\"{stem}\" for a fresh folder (auto-suffixed if that name is taken)"
                : freshPatch switch
                {
                    FreshPatchRemedy.NamedByPatchParam => $"dropping into= and passing patch=\"{stem}\" for a fresh patch (auto-suffixed if that name is taken)",
                    FreshPatchRemedy.CreatedByOmittingInto => "omitting into= to create it fresh",
                    _ => null,
                }));
    }

    /// <summary>The owned patches this refusal may offer: the capped, nearest-first <c>into=</c> spellings, how many the cap dropped, how many patches no spelling reaches, and whether the scan itself threw — three different ways the list comes back empty, only one of which is "houseCARL owns none".</summary>
    readonly record struct PatchCandidates(IReadOnlyList<string> Tokens, int BeyondCap, int Unreachable, bool NeedEsp,
                                           bool ScanFailed);

    /// <summary>One extend refusal, composed: what went wrong, then what to try, in ONE sentence with the nearest owned patches named inside it (Aaron, 2026-09-05). No in-place clause rides here: that lane is discoverable on the tool that declares it.</summary>
    static string ExtendRefusal(string wentWrong, string? noFreshRule, PatchCandidates candidates, string? fresh)
    {
        var nothingOwned = candidates.Tokens.Count == 0 && candidates.Unreachable == 0 && !candidates.ScanFailed;

        var tries = new List<string>();
        if (candidates.Tokens.Count > 0)
        {
            var t = candidates.Tokens;
            // The cap's drops are counted, so three names out of forty never read as the whole inventory.
            tries.Add((t.Count == 1 ? t[0] : string.Join(", ", t.Take(t.Count - 1)) + " or " + t[^1])
                    + (candidates.BeyondCap > 0 ? $" (+{candidates.BeyondCap} more)" : ""));
        }
        else if (candidates.Unreachable > 0)
            tries.Add($"renaming one of the {candidates.Unreachable} patch{(candidates.Unreachable == 1 ? "" : "es")} houseCARL owns "
                    + "in MO2, since no single into= spelling reaches any of them (their folder and plugin names collide)");
        // A scan that threw must NOT say houseCARL owns nothing.
        else if (candidates.ScanFailed)
            tries.Add("again once the mods folder can be read, since houseCARL could not scan it just now and so cannot "
                    + "name a patch or tell whether it owns any");
        if (fresh is not null) tries.Add(fresh);
        // A lane with no fresh route, on an install owning nothing, otherwise stops dead (#359, #380).
        if (tries.Count == 0 && noFreshRule is not null && nothingOwned)
            tries.Add($"making the patch first with a write that creates one ({ToolNames.Apply}, {ToolNames.Create} "
                    + $"or {ToolNames.Forward}), then naming it here");

        // Each fact is its own clause and only the LAST takes "and", so two of them cannot stack into ", and … , and …".
        var facts = new List<string> { wentWrong };
        if (noFreshRule is not null) facts.Add(noFreshRule);
        if (nothingOwned) facts.Add("houseCARL owns no patch " + (candidates.NeedEsp ? "holding a plugin yet" : "folder yet"));
        var said = facts.Count == 1 ? facts[0]
                 : string.Join(", ", facts.Take(facts.Count - 1)) + ", and " + facts[^1];
        return "cannot extend: " + said + (tries.Count == 0 ? "." : "; try " + string.Join(", or ", tries) + ".");
    }

    /// <summary>houseCARL-owned mod folders under ModsDir holding a plugin file named <paramref name="espFileName"/> at their root, as full .esp paths. Ownership-gated, so a user mod sharing the basename is never returned.</summary>
    List<string> OwnedFoldersHolding(string espFileName)
    {
        var hits = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_modsDir))
        {
            var esp = Path.Combine(dir, espFileName);
            if (File.Exists(esp) && IsHouseCarlOwned(dir)) hits.Add(esp);
        }
        return hits;
    }

    /// <summary>One owned patch folder as this refusal saw it; the plugins are read for EVERY owned folder, since whether a token resolves depends on which OTHER folders hold the same plugin.</summary>
    readonly record struct OwnedPatch(string Dir, string Name, IReadOnlyList<string> Plugins);

    /// <summary>The houseCARL-owned patches an extend refusal may name as <c>into=</c> spellings (#380). Every spelling emitted is one that RESOLVES back to the patch it stands for, run through this resolver's own arms against the folders read here, so a caller who takes one literally never meets a second refusal; a patch no token reaches is counted instead. Nearest <paramref name="stem"/> first, capped at three with the drops counted. Best-effort: an unreadable ModsDir yields no candidates rather than a partial set, and its failure is carried out.</summary>
    PatchCandidates OwnedPatchCandidates(bool needEsp, string stem)
    {
        const int cap = 3;
        var owned = new List<OwnedPatch>();
        var scanFailed = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsHouseCarlOwned(dir)) continue;
                var plugins = Directory.EnumerateFiles(dir)
                    .Where(f => PluginExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                owned.Add(new OwnedPatch(dir, Path.GetFileName(dir), plugins));
            }
        }
        // A PARTIAL set can make an ambiguous token look unambiguous, so it is dropped and the failure carried out.
        catch (IOException) { owned.Clear(); scanFailed = true; }
        catch (UnauthorizedAccessException) { owned.Clear(); scanFailed = true; }

        // Near-stem first (#380), by the shared suggester over folder AND plugin names, with an edit distance
        // breaking the tie it leaves when nothing clears its relevance bar and it answers EMPTY.
        var near = PluginNameSuggest.Nearest(stem, owned.SelectMany(o => o.Plugins.Prepend(o.Name)), max: 32);
        int NearRank(string name)
        {
            for (int i = 0; i < near.Count; i++)
                if (near[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
        int Distance(string name) => PluginNameSuggest.StemDistance(stem, name);
        int Rank(OwnedPatch o) => o.Plugins.Prepend(o.Name).Select(NearRank).Min();
        int Near(OwnedPatch o) => o.Plugins.Prepend(o.Name).Select(Distance).Min();

        var rows = new List<string>();
        var beyondCap = 0;
        var unreachable = 0;
        foreach (var o in owned.OrderBy(Rank).ThenBy(Near))
        {
            if (needEsp && o.Plugins.Count == 0) continue;
            var tokens = ReachingTokens(owned, o, needEsp);
            if (tokens.Count == 0) { unreachable++; continue; }             // no spelling reaches it — never offer a false one
            // Nearest first inside a folder too, since the cap decides which of several spellings survives.
            foreach (var t in tokens.OrderBy(NearRank).ThenBy(Distance))
                if (rows.Count < cap) rows.Add($"into=\"{t}\""); else beyondCap++;
        }
        return new PatchCandidates(rows, beyondCap, unreachable, needEsp, scanFailed);
    }

    /// <summary>The <c>into=</c> spellings for one owned patch that this resolver actually routes back to it: the folder's own name first, else the PLUGIN filenames one each, where that name resolves elsewhere or the record lane could not tell which plugin to extend. Empty when nothing reaches it.</summary>
    static List<string> ReachingTokens(List<OwnedPatch> owned, OwnedPatch target, bool needEsp)
    {
        var tokens = new List<string>();
        if (Reaches(target.Name)) tokens.Add(target.Name);
        else
            foreach (var p in target.Plugins)
                if (Reaches(p)) tokens.Add(p);
        return tokens;

        // The three arms above, off the folders in hand; on the record lane a token must also land on ONE plugin.
        bool Reaches(string token)
        {
            var s = PatchStem(token);
            var esp = s + ".esp";
            var canonicalName = ModFolderName(s);
            string? hit = null;
            var canon = owned.FirstOrDefault(o => o.Name.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
            if (canon.Dir is not null && (!needEsp || canon.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)))
                hit = canon.Dir;
            if (hit is null)
            {
                var byEsp = owned.Where(o => o.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)).ToList();
                if (byEsp.Count > 1) return false;                          // the ambiguous arm refuses instead
                if (byEsp.Count == 1) hit = byEsp[0].Dir;
            }
            if (hit is null)
                foreach (var cand in new[] { Path.GetFileName(token.Trim()), canonicalName })
                {
                    var named = owned.FirstOrDefault(o => o.Name.Equals(cand, StringComparison.OrdinalIgnoreCase));
                    if (named.Dir is not null) { hit = named.Dir; break; }
                }
            if (hit is null || !hit.Equals(target.Dir, StringComparison.OrdinalIgnoreCase)) return false;
            return !needEsp
                || target.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)   // the <stem>.esp fast path
                || target.Plugins.Count == 1;                                        // else the folder's sole plugin
        }
    }

    /// <summary>A houseCARL-owned mod folder named exactly <paramref name="rawName"/> or "<c>houseCARL - &lt;rawName&gt;</c>" — the folder catch-all behind <c>into=</c>. Bare name only, so it cannot escape ModsDir; null when no such folder is owned.</summary>
    string? ResolveOwnedFolderByName(string rawName)
    {
        var bare = Path.GetFileName(rawName.Trim());
        foreach (var cand in new[] { bare, ModFolderName(PatchStem(rawName)) })
        {
            if (string.IsNullOrEmpty(cand)) continue;
            var folder = Path.Combine(_modsDir, cand);
            if (Directory.Exists(folder) && IsHouseCarlOwned(folder)) return folder;
        }
        return null;
    }

    /// <summary>A mod folder is houseCARL-owned iff its <c>meta.ini</c> carries the marker; fail-safe, so a missing or stripped one reads as NOT owned. Ownership contract in docs/architecture/output-and-artifacts.md.</summary>
    static bool IsHouseCarlOwned(string folder) => HousecarlOwnerMeta.MarksOwned(folder);

    /// <summary>Write the new mod folder's <c>meta.ini</c>: the <c>[houseCARL]</c> ownership marker plus a minimal <c>[General]</c> for MO2's display.</summary>
    static void WriteOwnerMeta(string folder, string plugin)
    {
        var content =
            "[General]\r\n" +
            "gameName=skyrimse\r\n" +
            "modid=0\r\n" +
            "version=1.0\r\n" +
            "category=0\r\n" +
            "comments=Generated by houseCARL - load-order patch\r\n" +
            "\r\n" +
            HousecarlOwnerMeta.Section + "\r\n" +
            "generated=true\r\n" +
            $"plugin={plugin}\r\n" +
            $"created={DateTime.UtcNow:o}\r\n" +
            "\r\n" +
            "[installedFiles]\r\n" +
            "size=0\r\n";
        File.WriteAllText(Path.Combine(folder, "meta.ini"), content);
    }

    /// <summary>Is a located file the copy the MO2 install serves for its filename, and if not, why not? One half of "does the game load this file", independent of <see cref="TickStanding"/>, since a copy can be both shadowed and unticked with a remedy each. NotAnInstallCopy is deliberately the zero value.</summary>
    internal enum ServedStanding
    {
        /// <summary>The path is outside every install root, or no MO2 layer provides this exact file.</summary>
        NotAnInstallCopy = 0,
        /// <summary>THIS file is the copy the install serves — the first hit from an enabled layer.</summary>
        Serves,
        /// <summary>This copy's own layer is enabled, but a HIGHER-priority layer provides the same filename. Remedy: raise this mod's priority, or address the copy that wins.</summary>
        Shadowed,
        /// <summary>This copy sits in a mod folder MO2 knows about and has switched OFF. Remedy: switch it on, re-sort.</summary>
        ModDisabled,
        /// <summary>This copy sits in a folder modlist.txt does not mention, so MO2 has not registered it. Remedy: refresh MO2 — distinct from <see cref="ModDisabled"/>, which has something in MO2's list to switch.</summary>
        ModUnregisteredLayer,
    }

    /// <summary>Is a plugin filename ticked to load — the other half of "does the game load this file", a different fact from its mod folder's switch, which is the confusion this split exists to end.</summary>
    internal enum TickStanding
    {
        /// <summary>plugins.txt and loadorder.txt do not mention this filename at all — MO2 has not registered it.</summary>
        Unregistered = 0,
        /// <summary>`*`-prefixed in plugins.txt — checked.</summary>
        Ticked,
        /// <summary>A base-game or CC master: force-loaded and never listed in plugins.txt, so absence there means loaded.</summary>
        Implicit,
        /// <summary>Listed in plugins.txt WITHOUT the `*` — present but unchecked. The game does not load it.</summary>
        Unticked,
    }

    /// <summary>One located plugin file, or why not; exactly one of Path, Ambiguous or Error is set. The two standings are carried separately rather than collapsed into one boolean, so a renderer can explain rather than classify; <see cref="Enabled"/> is the derived "the game loads this file".</summary>
    /// <param name="CauseDetail">For <see cref="ServedStanding.Shadowed"/>, the where-label of the copy that IS served; for the two layer-off standings, the mod FOLDER NAME alone, never the hit label, which carries its own remedy and would make the sentence say it twice.</param>
    /// <param name="WhereNamesLayer">Does <paramref name="Where"/> already identify which layer holds this copy? Carried as a fact from each lane rather than re-derived by string-comparing the labels, which holds for one lane and silently fails for another.</param>
    internal readonly record struct PluginLocateResult(
        string? Path, string Where, ServedStanding Served, TickStanding Tick, string? CauseDetail,
        bool WhereNamesLayer,
        IReadOnlyList<PluginFileHit>? Ambiguous, string? Error)
    {
        /// <summary>The game loads THIS file: it is the served copy AND its plugin is ticked, implicit masters counting as ticked. Both halves, or one physical file answers differently depending on how it was addressed.</summary>
        public bool Enabled => Served == ServedStanding.Serves && Tick is TickStanding.Ticked or TickStanding.Implicit;

        /// <summary>Why the game does not load this file, null when <see cref="Enabled"/> or when nothing was located, composed here once so no two renderers drift. Both clauses are emitted when both apply, except that the unregistered clause is suppressed when the served half already explains the absence.</summary>
        public string? WhyNotActive
        {
            get
            {
                if (Enabled || Path is null) return null;
                var name = System.IO.Path.GetFileName(Path);
                var parts = new List<string>(2);
                switch (Served)
                {
                    case ServedStanding.Shadowed:
                        // CauseDetail is always set here: Shadowed means a served hit exists, and it is another copy.
                        parts.Add($"this copy is SHADOWED — {CauseDetail} provides the copy the game loads");
                        break;
                    // The layer-off standings name the folder only when Where does not, never echoing a label.
                    case ServedStanding.ModDisabled:
                        parts.Add(WhereNamesLayer
                            ? "that mod folder is switched OFF in MO2 — switch it on, then re-sort"
                            : $"it is provided by mod '{CauseDetail}', which is switched OFF in MO2 — switch it on, then re-sort");
                        break;
                    case ServedStanding.ModUnregisteredLayer:
                        parts.Add(WhereNamesLayer
                            ? "MO2 has not registered that mod folder — refresh MO2, then tick the plugin and sort"
                            : $"it is provided by mod '{CauseDetail}', which MO2 has not registered — refresh MO2, then tick the plugin and sort");
                        break;
                    case ServedStanding.NotAnInstallCopy:
                        // States what was CHECKED, not a verdict: a junction route to the same install lands here too.
                        parts.Add("no MO2 layer was found providing this exact path");
                        break;
                }
                if (Tick == TickStanding.Unticked)
                    parts.Add($"'{name}' is UNTICKED in plugins.txt (MO2's right pane)");
                else if (Tick == TickStanding.Unregistered && Served == ServedStanding.Serves)
                    parts.Add($"'{name}' is not registered in MO2's load order (refresh MO2 to pick it up)");
                return parts.Count == 0 ? null : string.Join("; and ", parts);
            }
        }
    }

    /// <summary>Judge the served half for one located file, against the first hit from an ENABLED layer — the rule the real order is built by, not merely the first hit, since the locate also walks folders the order never consults. Compared by full path, because a backup and the live copy share a filename.</summary>
    static (ServedStanding Served, string? Detail) JudgeServed(
        Mo2Composition comp, IReadOnlyList<PluginFileHit> located, string fullPath)
    {
        var served = located.FirstOrDefault(h => h.Enabled);
        if (served is not null && SamePluginFile(served.Path, fullPath)) return (ServedStanding.Serves, null);
        var own = located.FirstOrDefault(h => SamePluginFile(h.Path, fullPath));
        if (own is null) return (ServedStanding.NotAnInstallCopy, null);          // outside the install, or unreachable by string compare
        // Its own layer is ON but something else serves the name, and the useful pointer is the copy that WINS.
        if (own.Enabled) return (ServedStanding.Shadowed, served?.Where);
        // Its layer is off; which kind decides the remedy, read from the mod list, never from the hit's label text.
        var folder = Path.GetFileName(Path.GetDirectoryName(own.Path) ?? "") ?? "";
        bool listedOff = comp.DisabledMods.Any(m => m.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return (listedOff ? ServedStanding.ModDisabled : ServedStanding.ModUnregisteredLayer, folder);
    }

    /// <summary>Judge the tick half for one plugin filename from the profile text files, kept beside <see cref="JudgeServed"/> so no lane computes either half its own way.</summary>
    static TickStanding JudgeTick(Mo2Composition comp, string fileName)
    {
        if (comp.ActivePluginNames.Contains(fileName)) return TickStanding.Ticked;
        foreach (var x in comp.ImplicitPluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Implicit;
        foreach (var x in comp.InactivePluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Unticked;
        return TickStanding.Unregistered;
    }

    /// <summary>The on-disk plugin-locate contract, shared by every lane that resolves a plugin by name so no two can diverge: a direct path is used verbatim, else the filename is found across the whole install, with <paramref name="mod"/> narrowing a name several folders provide and ambiguity coming back structured. <paramref name="offerModParam"/> is false for a caller that does not declare <c>mod=</c>.</summary>
    internal static PluginLocateResult LocatePluginFileOnDisk(Mo2Composition comp, Mo2Roots roots, string plugin, string? mod,
                                                              bool offerModParam = true) =>
        LocatePluginFileOnDisk(comp, roots.ModsDir, roots.DataDir, roots.OverwriteDir, plugin, mod, offerModParam);

    /// <summary>The same locate over the three install roots given one by one.</summary>
    internal static PluginLocateResult LocatePluginFileOnDisk(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string plugin, string? mod,
        bool offerModParam = true)
    {
        // Every lane below returns the (served, tick) pair through the same two helpers, never its own way.
        if (LooksLikePath(plugin))
        {
            if (!File.Exists(plugin))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null, $"no file at path '{plugin}'.");
            var full = Path.GetFullPath(plugin);
            // Computed for a direct path, never assumed, since one can name the live copy of an enabled plugin; a
            // path in no install root skips the sweep, and one reached by junction reads as NotAnInstallCopy.
            var fnPath = Path.GetFileName(full);
            var located = IsUnderAnyInstallRoot(full, modsDir, dataDir, overwriteDir)   // outside every root ⇒ can't be the install's copy; skip the scan
                ? Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fnPath)
                : Array.Empty<PluginFileHit>();
            var (servedStanding, detail) = JudgeServed(comp, located, full);
            // WhereNamesLayer: FALSE — "direct path" identifies no layer, so a layer-off cause must name the folder.
            return new(full, "direct path", servedStanding, JudgeTick(comp, fnPath), detail, false, null, null);
        }
        if (!string.IsNullOrWhiteSpace(mod))
        {
            var fn = Path.GetFileName(plugin);
            var cand = Path.Combine(modsDir, mod.Trim(), fn);
            if (!File.Exists(cand))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null,
                           $"mod folder '{mod.Trim()}' under ModsDir does not provide '{fn}'.");
            // Both halves here too: an enabled mod's copy can still be shadowed by a higher-priority one.
            var (modServed, modDetail) = JudgeServed(
                comp, Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fn), cand);
            // WhereNamesLayer is true: "mod 'X'" names the folder, though it carries no state qualifier.
            return new(cand, $"mod '{mod.Trim()}'", modServed, JudgeTick(comp, fn), modDetail, true, null, null);
        }
        var hits = Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, plugin);
        if (hits.Count == 0)
            return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null,
                $"'{Path.GetFileName(plugin)}' is in no mod folder (enabled, disabled, or not-yet-listed in MO2), the overwrite folder, or the game Data folder. Check the filename, pass an absolute path"
                + (offerModParam ? ", or (if it's an MO2 mod) the exact folder via mod=." : "."));
        if (hits.Count > 1) return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, hits, null);
        var (oneServed, oneDetail) = JudgeServed(comp, hits, hits[0].Path);
        // WhereNamesLayer: TRUE — Where IS the located hit's own label, folder and state both.
        return new(hits[0].Path, hits[0].Where, oneServed, JudgeTick(comp, Path.GetFileName(plugin)), oneDetail, true, null, null);
    }

    /// <summary>Does the user's `plugin` argument denote a PATH, used verbatim, rather than a bare filename located in the MO2 folders? True if rooted or carrying a directory separator.</summary>
    internal static bool LooksLikePath(string s) => Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/');

    /// <summary>Do two paths denote the same plugin file? A full-path compare, never a filename one, since a backup and the live copy share a name.</summary>
    internal static bool SamePluginFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Is <paramref name="fullPath"/> inside any MO2 or game root? Used only to skip work, so the enabled/disabled classification stays with the shared locate and is never re-derived here.</summary>
    static bool IsUnderAnyInstallRoot(string fullPath, params string[] roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* an unparseable root simply isn't a match — never a false 'inside' */ }
        }
        return false;
    }
}

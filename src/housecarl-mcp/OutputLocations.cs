using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>The resolved output location for a NON-.esp rider: the directory to WRITE into, the mod-folder ROOT
    /// (what the cleanup operates on), and whether THIS call created the folder fresh, versus reusing an into= folder,
    /// which the user owns and cleanup never touches). For the .bsa/extract riders OutputDir == ModFolder; for the
    /// compile/decompile riders OutputDir is a subfolder (<c>Scripts\</c> / <c>Source\Scripts\</c>) under ModFolder.
    /// <paramref name="Stem"/> is the folder's own name without the "houseCARL - " prefix — the name a lane whose
    /// artifact takes the folder's name (the .bsa) gives that artifact.</summary>
    public readonly record struct RiderFolder(string OutputDir, string ModFolder, bool CreatedFresh, string Stem);

    /// <summary>How ONE rider lane names the mod folder it creates — the calling tool's own statement, the way
    /// <see cref="FreshPatchRemedy"/> is for the record lanes. <paramref name="Param"/> is the parameter that tool
    /// actually declares for the folder's name, which is <c>patch=</c> on every tool that writes one. A lane whose
    /// <c>into=</c> can never be non-empty passes null, and keeps the weakest true remedy.
    /// <paramref name="RefuseTaken"/> is set by a lane whose ARTIFACT takes the folder's name and whose exact
    /// basename is load-bearing — the .bsa, which the game auto-loads only under its plugin's basename: a taken stem
    /// refuses by name rather than writing an archive nothing loads. A lane whose artifact is named independently of
    /// the folder (compiled scripts, extracted files) leaves it null and keeps auto-suffixing.</summary>
    public readonly record struct RiderNaming(string Param, StemRefusal? RefuseTaken = null);

    /// <summary>Resolve a houseCARL-owned mod folder under ModsDir for a non-.esp output — compiled scripts, a packed
    /// .bsa, extracted loose files — generalising the folder-per-patch model beyond the .esp write path. Either a
    /// fresh marker-stamped folder, named by <paramref name="defaultStem"/> when patchName is blank and auto-suffixed
    /// so a prior one is never clobbered, or <paramref name="into"/> an existing houseCARL-owned one. It refuses a
    /// folder houseCARL did not create. Derives ModsDir cheaply by reading ModOrganizer.ini, with no index build, and
    /// throws the unconfigured prompt when there is no instance. The returned
    /// <see cref="RiderFolder.CreatedFresh"/> flag drives the cleanup on a failure. No lane here writes a plugin —
    /// scripts, an archive or loose files only — so none takes a plugin-shadow refusal; a plugin's own name follows
    /// its folder stem through <see cref="ResolveOutputPath"/> instead.</summary>
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
                // The same shared extend resolver as the .esp write path: a renamed houseCARL patch folder is found by
                // the .esp it holds or by its new name, so every rider's into= behaves exactly like a record into=.
                // needEsp:false because a rider targets the FOLDER itself, writing scripts, a .bsa or loose files
                // into it rather than an .esp, so no <stem>.esp need be present.
                // The fresh-patch remedy is the CALLING LANE's, not this method's: omitting into= here does create a
                // fresh folder, but which parameter names it, and what it is called when nobody names it, differ per
                // rider — so the lane hands both in and the sentence is true of it (#357). A lane that hands in
                // nothing keeps the weakest true remedy rather than a shared one that is wrong for it.
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

    /// <summary>The <c>Scripts\</c> output folder for a compiled .pex: a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>Scripts\</c> subfolder, which MO2 deploys into the game's
    /// Data\Scripts. Carries the mod-folder root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
        var scripts = Path.Combine(f.ModFolder, "Scripts");
        Directory.CreateDirectory(scripts);
        return f with { OutputDir = scripts };
    }

    /// <summary>The out_path= escape hatch: the user names where the compiled .pex lands instead of houseCARL
    /// cutting a fresh folder-per-patch mod folder. out_path is a mod-folder ROOT and houseCARL appends
    /// <c>Scripts\</c>, matching <see cref="ResolveCompiledScriptFolder"/> and MO2's deploy model so the .pex
    /// actually loads, with a guard against appending a second Scripts\ when one is already there. It cuts no
    /// houseCARL mod folder, and the folder is user-owned, so the returned <see cref="RiderFolder"/> carries
    /// CreatedFresh=false and cleanup never deletes it on a failed compile. <paramref name="deployWarning"/> is
    /// non-null when the final Scripts\ path is none of a mod's own Scripts\, the MO2 overwrite folder, or the game's
    /// Data, because the .pex compiles but the game will not auto-load it from there. Refuses loudly on an unusable
    /// out_path — a malformed path, or one naming an existing file.</summary>
    public RiderFolder ResolveExplicitScriptFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "Scripts", ScriptOutputContract, out deployWarning);

    /// <summary>The same out_path= contract for the SEQ rider: the user names a mod-folder root and houseCARL
    /// appends <c>SEQ\</c>. It exists because the .seq output model otherwise assumes the plugin it serves lives in a
    /// houseCARL folder, which the in-place .esp lane inverts — the .esp in the mod's own folder, the .seq in a
    /// different mod entirely — and a .seq in an un-enabled or wrong folder leaves the quest silently dead.
    /// <para>The <c>into=</c> ownership check is deliberately untouched: letting into= name a folder houseCARL did
    /// not create would put its patch-folder machinery inside a third party's mod. This lane instead writes a new
    /// sidecar file into a folder the user owns, cutting no houseCARL folder and bypassing cleanup
    /// (<see cref="RiderFolder.CreatedFresh"/> = false).</para></summary>
    public RiderFolder ResolveExplicitSeqFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "SEQ", SeqOutputContract, out deployWarning);

    /// <summary>The shared body of the out_path= lanes, one artifact per caller: refuse a path that is not absolute
    /// or is otherwise unusable, normalize the root, apply <paramref name="contract"/>, which appends <paramref name="sub"/> with the
    /// double-segment guard and decides deployability, create the folder, and hand back a user-owned RiderFolder.
    /// One body rather than one per rider, so the rules cannot drift per artifact.</summary>
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
            // A plain message when the folder cannot be created — the subfolder already exists as a file, or the path
            // is read-only — instead of letting the IO or access exception reach the generic internal-failure
            // handler, which would read as a houseCARL bug rather than bad input.
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"out_path: couldn't create the output folder '{outDir}' ({ex.Message}). Check the path and that it's writable."); }
            deployWarning = warn;
            // ModFolder is the mod-folder root — inert here, since cleanup is bypassed by CreatedFresh=false, but
            // kept accurate: when the user pointed at the subfolder, the root is its parent; otherwise the path they
            // gave IS the root.
            var modRoot = appended ? root : (Path.GetDirectoryName(outDir.TrimEnd('\\', '/')) ?? outDir);
            return new RiderFolder(outDir, modRoot, CreatedFresh: false, FolderStem(modRoot));   // user-owned: residue cleanup never touches it
        }
    }

    /// <summary>Pure, filesystem-free resolution of the out_path= contract, so it is testable without an MO2
    /// instance. Appends <c>Scripts\</c> to a mod-folder root, taking a root that already ends in a Scripts segment
    /// as-is — any case, trailing separator tolerated — rather than doubling it. <paramref name="outputDir"/> is
    /// expected absolute. Returns the final Scripts dir, whether Scripts\ was appended, and a deployWarning when the
    /// result is none of a mod folder under <paramref name="modsDir"/>, the overwrite folder, or
    /// <paramref name="dataDir"/>. <paramref name="overwriteDir"/> counts as deployable because MO2 maps the
    /// overwrite folder's contents onto the Data root at top priority.</summary>
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

    /// <summary><see cref="ScriptOutputContract"/>'s twin for the <c>.seq</c>: the same pure path contract with
    /// <c>SEQ\</c> appended, and a warning worded for what a mis-placed .seq costs. The stakes differ from the .pex's:
    /// a script that does not deploy leaves the old behaviour, while a .seq the engine never reads leaves every
    /// start-game-enabled quest in that plugin silently not starting.</summary>
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

    /// <summary>The shared pure core of the out_path= contracts: append <paramref name="sub"/> to a mod-folder
    /// root, taking a root already ending in that segment as-is rather than doubling it, and decide deployability.
    /// MO2 overlays a mod folder's CONTENTS onto the game Data root, so a deployable folder is exactly
    /// <c>&lt;mods&gt;\&lt;modFolder&gt;\&lt;sub&gt;</c>, with the mod folder a direct child of mods and the
    /// subfolder directly under it. A bare <c>&lt;mods&gt;\&lt;sub&gt;</c> has no mod folder, and a nested
    /// <c>&lt;mods&gt;\X\Sub\&lt;sub&gt;</c> lands at Data\Sub\… rather than Data\&lt;sub&gt;, so neither loads and
    /// both warn. A direct game install loads exactly <c>&lt;data&gt;\&lt;sub&gt;</c>, as does
    /// <c>&lt;overwriteDir&gt;\&lt;sub&gt;</c>. <paramref name="outputDir"/> is expected absolute. The per-artifact
    /// sentence stays with each caller: the rule is shared, the consequence is not.</summary>
    static (string dir, bool appendedSub, bool deployable) SubfolderOutputContract(
        string outputDir, string sub, string modsDir, string dataDir, string overwriteDir = "")
    {
        // A drive root keeps its separator: trimming "C:\" gives "C:", and combining that with a subfolder yields the
        // drive-RELATIVE "C:SEQ", which Windows resolves against the process's current directory on that drive — so
        // the folder is created somewhere else entirely under a name that looks absolute.
        var root = IsRoot(outputDir) ? outputDir : outputDir.TrimEnd('\\', '/');
        bool already = Path.GetFileName(root).Equals(sub, StringComparison.OrdinalIgnoreCase);
        var dir = already ? root : Path.Combine(root, sub);
        return (dir, !already,
            IsModDeployFolder(dir, modsDir) || IsDataDeployFolder(dir, dataDir) || IsDataDeployFolder(dir, overwriteDir));
    }

    /// <summary>Is this path a filesystem root whose trailing separator is part of its meaning — <c>C:\</c>, where
    /// trimming yields the drive-relative <c>C:</c>? A bare UNC share answers true as well, since
    /// <c>GetPathRoot</c> returns it unchanged, which is harmless because trimming it changes nothing. The case this
    /// helper exists for is the drive root.</summary>
    static bool IsRoot(string path)
    {
        try { return string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>A deploy folder MO2 actually serves: exactly <c>&lt;modsDir&gt;\&lt;modFolder&gt;\&lt;sub&gt;</c>,
    /// with the mod folder a direct child of the mods root and the subfolder directly under it. MO2 maps a mod
    /// folder's contents onto the Data root, so <c>&lt;mods&gt;\Scripts</c> has no mod and
    /// <c>&lt;mods&gt;\X\Sub\Scripts</c> lands at Data\Sub\Scripts. The rule is about shape, not the segment's name,
    /// so every lane shares it. An empty mods root is false. Case-insensitive and normalized.</summary>
    static bool IsModDeployFolder(string outDir, string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return false;
        var modFolder = Path.GetDirectoryName(outDir.TrimEnd('\\', '/'));       // expect <mods>\<modFolder>
        return modFolder is not null && PathEquals(Path.GetDirectoryName(modFolder), modsDir);
    }

    /// <summary>A direct game install loads exactly <c>&lt;dataDir&gt;\&lt;sub&gt;</c> (not Data\Sub\…). Empty data dir →
    /// false. Case-insensitive, normalized.</summary>
    static bool IsDataDeployFolder(string outDir, string dataDir)
    {
        if (string.IsNullOrEmpty(dataDir)) return false;
        return PathEquals(Path.GetDirectoryName(outDir.TrimEnd('\\', '/')), dataDir);
    }

    /// <summary>Case-insensitive equality of two paths after full-path normalization + trailing-separator trim (no
    /// filesystem access). A null left side (no parent — e.g. a drive root) is never equal.</summary>
    static bool PathEquals(string? a, string b)
    {
        if (a is null) return false;
        return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>Source\Scripts\</c> output folder for a decompiled .psc — the SE-canonical source layout, and
    /// the same default patch stem as the compile lane so decompile, edit and compile accumulate in one folder via
    /// <c>into=</c>. Carries the root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveDecompiledSourceFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
        var src = Path.Combine(f.ModFolder, "Source", "Scripts");
        Directory.CreateDirectory(src);
        return f with { OutputDir = src };
    }

    /// <summary>The <c>out_path=</c> lane for a decompiled .psc: the caller names a folder and the .psc lands
    /// straight in it. Nothing is appended — a .psc is source a compiler reads, never a file the game loads — so
    /// this is the <c>bsa_extract</c> shape rather than <see cref="ResolveExplicitScriptFolder"/>'s, and there is no
    /// deployability question to answer. The folder is the caller's, so the returned
    /// <see cref="RiderFolder"/> carries CreatedFresh=false and residue cleanup never deletes it. Refuses a path
    /// that is not absolute, and one naming an existing file; creates the folder when it is missing. Reads no
    /// instance state — the .psc lands entirely outside the MO2 instance — so it takes no lock, and the tool lets
    /// this lane run with no instance configured, unlike the default one. What the instance would have added is the
    /// class hierarchy's mods-tree top-up, which the tool states as degraded rather than requiring.</summary>
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

    /// <summary>A non-.esp rider that failed after creating a fresh houseCARL mod folder cleans up after itself, the
    /// same "a refusal leaves no orphan folder" principle the .esp lane follows. If the fresh folder is genuinely
    /// empty — holding nothing but our own meta.ini marker anywhere in its tree — it is deleted, so "no output
    /// written" is true of the disk; if real output landed, the folder stays and its path is returned so the caller
    /// can name it, because houseCARL never deletes content it did not recognise as its own. A reused into= folder
    /// is never touched: the user owns it. Returns the leftover path to name, or null. Best-effort: a cleanup hiccup
    /// never masks the rider's own outcome.</summary>
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

    /// <summary>The <c>SEQ\</c> output folder for a generated <c>.seq</c>: a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>SEQ\</c> subfolder, which MO2 deploys into the game's
    /// <c>Data\SEQ</c>. Carries the mod-folder root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveSeqFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_SEQ", new RiderNaming("patch"));
        var seq = Path.Combine(f.ModFolder, "SEQ");
        Directory.CreateDirectory(seq);
        return f with { OutputDir = seq };
    }

    /// <summary>If <paramref name="pluginPath"/> lives in a houseCARL-owned mod folder directly under ModsDir, return
    /// that folder's patch stem, so the <c>.seq</c> defaults into the same folder as the <c>.esp</c>: one mod to
    /// enable, and no second folder the user might forget, which would leave the quest silently dead. Only when the
    /// folder is the canonical one for this plugin, so a later <c>into=</c> resolves to exactly it; otherwise null,
    /// and the caller cuts a fresh folder.</summary>
    string? OwnedPluginFolderStem(string pluginPath)
    {
        var dir = Path.GetDirectoryName(pluginPath);
        if (dir is null || Path.GetDirectoryName(dir) is not { } parent || !PathEquals(parent, _modsDir)) return null;
        if (!IsHouseCarlOwned(dir)) return null;
        var stem = PatchStem(Path.GetFileName(pluginPath));
        return Path.GetFileName(dir).Equals(ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? stem : null;
    }

    /// <summary>Write a plugin's start-game-enabled-quest <c>.seq</c>. Opens <paramref name="plugin"/>, collects
    /// every start-game-enabled quest it defines, and writes <c>&lt;ModFolder&gt;\SEQ\&lt;plugin&gt;.seq</c> — the
    /// file the engine reads to actually start those quests, since the flag alone does nothing — under the same
    /// crash-atomic, non-destructive folder-per-patch model as the other riders. The output folder defaults to the
    /// plugin's own houseCARL folder when it lives in one, so the .seq deploys with the .esp; else a fresh folder, or
    /// <paramref name="into"/> / <paramref name="patchName"/> when given. A plugin with no such quests writes nothing
    /// and cuts no folder, stated explicitly rather than as a silent empty file. Serialized on the write gate.
    /// <para><paramref name="outputDir"/> is the same out_path= contract the compile lane carries: the user names a
    /// mod-folder root — typically the plugin's own mod, after an in-place .esp edit — and the .seq lands in its
    /// <c>SEQ\</c>. It wins over <paramref name="patchName"/> and <paramref name="into"/>, and cuts no houseCARL
    /// folder.</para>
    /// <para>A destination already holding exactly these bytes is reported as such and not rewritten, because the
    /// workflow regenerates the .seq after every in-place edit and the answer is byte-identical nearly every time.
    /// The no-op is stated explicitly: an unstated skip is indistinguishable from a silent failure.</para></summary>
    public SeqOutcome WriteSeq(string plugin, string? patchName, string? into, string? outputDir = null)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return SeqOutcome.Fail("no source given. Pass source= the plugin whose start-game-enabled quests need a .seq — its filename (e.g. 'MyQuestMod.esp') or an absolute path.");
        plugin = plugin.Trim().Trim('"');

        // Source resolution: a bare filename is located across the MO2 folders — enabled, disabled, not-yet-listed,
        // overwrite and game Data — through the same shared contract every other lane uses, so two tools can never
        // find different files for one name. An absolute path is used verbatim. The arm that resolved is reported
        // rather than silent: which copy was read decides which .seq you get.
        string pluginPath, resolvedFrom;
        try
        {
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            var comp = Mo2LoadOrder.ReadComposition(profileDir);        // cheap text parse — no index build
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, null, offerModParam: false);
            // The locate contract's refusal names what it could not find; this adds what THIS tool accepts, so the
            // caller is not left to infer that a bare filename is allowed at all.
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

        lock (_writeGate)                                                // one write at a time: build, resolve, commit
        {
            if (ConfigPromptOrNull() is { } cfgPrompt) return SeqOutcome.Fail(cfgPrompt);   // need ModsDir for the output folder
            lock (_gate) EnsurePathsDerived();                          // derive ModsDir for the owned-folder check; lock order is _writeGate then _gate

            // Build the .seq from the plugin: a read-only overlay, disposed inside, so no handle is held at rest.
            SeqFile.SeqBuild built;
            try { built = SeqFile.Build(pluginPath); }
            catch (Exception ex)
            { return SeqOutcome.Fail($"could not read '{Path.GetFileName(pluginPath)}' as a plugin: {ex.Message}"); }

            // No start-game-enabled quests means no .seq is needed: write nothing, cut no folder, and say so.
            // It still carries UserChoseOutput, because the lane the caller named is a fact about the CALL, and
            // dropping it here would make the json twin contradict its own lane note.
            if (built.Quests.Count == 0)
                return new SeqOutcome(true, null, null, null, built.Quests, built.PluginFileName, false)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, UserChoseOutput = !string.IsNullOrWhiteSpace(outputDir) };

            // Output folder: out_path=, the user's own mod folder, wins; else the plugin's own houseCARL folder;
            // else a fresh one or an explicit into= / patch. The out_path arm cuts no houseCARL folder, so
            // the owned-folder default is not consulted there — the caller named the destination outright.
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

            // When the destination already holds exactly these bytes, report it and write nothing: the regenerate
            // loop rarely changes the answer, so the common case was a rewrite that changed nothing. Compared
            // against the bytes already built in memory, so this costs one read of a file that is typically a few
            // hundred bytes, and it is reported as its own state rather than folded into success.
            // One thing the skip must NOT take with it is the timestamp. The dialogue check's .seq staleness test
            // reads mtime, not content, so a .seq older than its plugin is reported stale even when byte-perfect,
            // and skipping the write after an in-place edit would leave a permanent advisory no tool could clear.
            // So an identical-but-older file has its timestamp refreshed. If the touch fails, fall THROUGH to the
            // real write rather than reporting a no-op that leaves the staleness test wrong.
            bool sameBytes = SameBytesOnDisk(dest, built.Bytes), identical = sameBytes, touched = false;
            if (identical && !RefreshSeqTimestamp(dest, pluginPath, out touched)) identical = false;
            if (identical)
                return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, Unchanged = true, TimestampRefreshed = touched,
                      UserChoseOutput = chosenOutput, DeployWarning = deployWarning };

            // Is there something here already? An out_path= destination is a folder houseCARL does not own, so the
            // file being replaced may be the mod's own shipped .seq, and "wrote" and "replaced yours" are different
            // facts about the disk.
            bool replaced = File.Exists(dest);
            // And whether anything was lost. The one path that reaches the write with sameBytes true is a timestamp
            // refresh that failed — a share that accepts the stamp without persisting it — and calling that
            // "overwritten, no backup is kept" would be an alarm about a file whose bytes were re-written identically.
            bool replacedSameBytes = replaced && sameBytes;

            // Crash-atomic write of <plugin>.seq under SEQ\, into a houseCARL-owned folder or the folder the caller
            // named in out_path=.
            try { AtomicFile.WriteAllBytes(dest, built.Bytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);             // nothing landed → a fresh folder is an orphan
                return SeqOutcome.Fail($"could not write '{seqName}': {ex.Message}"
                    + (residue is null ? "" : $" The freshly created folder was left at '{residue}'.")
                    // On the out_path lane cleanup is bypassed by design, since the folder is the user's, so the
                    // SEQ\ directory is still there and "nothing was written" is true of the file, not the disk.
                    // Worded for what is known — the folder is there and houseCARL will not remove it — because
                    // claiming this call created it would be false whenever the mod already ships a SEQ\ folder.
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

    /// <summary>Keep a skipped write honest against the mtime-based .seq staleness test: if
    /// <paramref name="seqPath"/> is byte-identical but older than <paramref name="pluginPath"/>, stamp it now.
    /// <paramref name="touched"/> reports whether a stamp was actually needed, so the response can say so rather than
    /// implying one silently happened. Returns false when the stamp was needed and failed, so the caller does the
    /// real write instead of reporting a no-op that leaves the dialogue check calling the file stale.</summary>
    static bool RefreshSeqTimestamp(string seqPath, string pluginPath, out bool touched)
    {
        touched = false;
        try
        {
            var seqTime = File.GetLastWriteTimeUtc(seqPath);
            var pluginTime = File.GetLastWriteTimeUtc(pluginPath);
            if (seqTime >= pluginTime) return true;                 // already newer — the staleness test is satisfied
            // Now is not necessarily enough: a plugin can be stamped in the FUTURE relative to this machine's clock —
            // a restored backup, a synced share, a dual-boot local-versus-UTC BIOS clock — and stamping the current
            // time there would mutate the file, report a refresh, and leave the comparison exactly as it was. Stamp
            // past the plugin, then verify, and let a stamp that did not achieve it fall through to the real write.
            var target = pluginTime > DateTime.UtcNow ? pluginTime.AddSeconds(1) : DateTime.UtcNow;
            File.SetLastWriteTimeUtc(seqPath, target);
            if (File.GetLastWriteTimeUtc(seqPath) < File.GetLastWriteTimeUtc(pluginPath)) return false;
            touched = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Does <paramref name="path"/> already hold exactly <paramref name="bytes"/>? Length first as the cheap
    /// discriminator, then a full compare. Any IO problem answers false: "could not prove it is identical" must fall
    /// through to the write, because a wrong true here leaves a stale .seq reported as current.</summary>
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

    // ---- decompiler class hierarchy (lazy, cached for process lifetime) ----------------------------------------

    Dictionary<string, string>? _classParents;
    string? _classParentsNote;
    bool _classParentsToppedUp;
    string? _classParentsTopUpMissing;
    readonly object _classParentsLock = new();

    /// <summary>Drop the cached hierarchy whenever <see cref="_modsDir"/> can have changed — an instance switch or a
    /// profile re-derive — because a stale tree's edges could suppress a cast the new order's hierarchy does not
    /// justify. Rebuilds lazily.</summary>
    void InvalidateClassParents()
    {
        lock (_classParentsLock)
        {
            _classParents = null; _classParentsNote = null;
            _classParentsToppedUp = false; _classParentsTopUpMissing = null;
        }
    }

    /// <summary>The decompiler's child-to-parent class map: the committed vanilla baseline beside the exe, plus
    /// loose .psc headers across the MO2 mods tree from mods that ship sources. The baseline is read once and cached;
    /// the mods-tree top-up is RETRIED on every call until it runs, so a baseline-only map is never cached as if it
    /// were complete and the caller can say so every time. It is a soft input by construction — missing pieces mean
    /// explicit casts in the output, never wrong code — and the result names both degraded modes: a missing or
    /// unreadable baseline, and a top-up that did not happen or read only part of the tree, with the cause (no
    /// instance, an instance that does not resolve, a mods folder that is gone, one that cannot be listed, files
    /// under it that cannot be read). A published map is never mutated: the top-up fills a copy and replaces the
    /// published one in a single assignment. The input pex's own folder is topped up per call by
    /// the caller, since it varies per input. Paths derive FIRST, under the gate, because in instance mode ModsDir is
    /// lazy. Lock order is _gate then _classParentsLock.</summary>
    public ClassParents ClassParentsForDecompile()
    {
        bool configured;
        string? deriveError = null;
        lock (_gate)
        {
            configured = _configured;
            // An instance that does not resolve is not fatal here — the tool's own gate decides whether the call can
            // proceed — but the reason is carried out, because it is why the top-up below cannot run.
            if (configured)
                try { EnsurePathsDerived(); }
                catch (Exception ex) { deriveError = ex.Message; }
        }
        lock (_classParentsLock)
        {
            if (_classParents is null)
            {
                var (edges, note) = HousecarlCore.PapyrusClassParents.LoadBaseline(
                    Path.Combine(AppContext.BaseDirectory, "vanilla-class-parents.json"));
                _classParents = edges;
                _classParentsNote = note;
            }
            if (!_classParentsToppedUp)
            {
                string? missing =
                    !configured ? "no MO2 instance is configured"
                    : deriveError is not null ? $"the MO2 instance does not resolve ({deriveError})"
                    : string.IsNullOrEmpty(_modsDir) ? "the instance has no mods folder"
                    : !Directory.Exists(_modsDir) ? $"the mods folder '{_modsDir}' does not exist"
                    : null;
                if (missing is null)
                {
                    // Publish-once: the walk fills a COPY and the finished map replaces the published one, so a
                    // concurrent reader either sees the old map or the new one, never one being written.
                    var topped = new Dictionary<string, string>(_classParents, StringComparer.OrdinalIgnoreCase);
                    var scan = HousecarlCore.PapyrusClassParents.AddFromPscHeaders(topped, new[] { _modsDir });
                    if (scan.RootsUnreadable > 0)
                        // Nothing was read from the tree: the copy is dropped and the walk is retried next call.
                        missing = $"the mods folder '{_modsDir}' could not be listed";
                    else
                    {
                        _classParents = topped;
                        _classParentsToppedUp = true;
                        if (scan.FilesFailed > 0)
                            missing = $"{scan.FilesFailed} of {scan.FilesSeen} .psc file(s) under '{_modsDir}' could not be read";
                    }
                }
                _classParentsTopUpMissing = missing;
            }
            return new ClassParents(_classParents, _classParentsNote, _classParentsTopUpMissing);
        }
    }

    /// <summary>The MO2 mod-folder name for a patch stem. The "houseCARL - " prefix groups our patches in MO2's left
    /// pane and is the human-visible ownership signal (the meta.ini marker is the structural one).</summary>
    static string ModFolderName(string stem) => "houseCARL - " + stem;

    /// <summary>The stem a mod folder carries: "houseCARL - &lt;stem&gt;" without the prefix, and the folder's own
    /// name when it has none — a renamed patch folder, or a user-owned one. The inverse of
    /// <see cref="ModFolderName"/> where one applies, and the name an artifact takes from its folder.</summary>
    static string FolderStem(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string prefix = "houseCARL - ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

    /// <summary>Plugin extensions stripped from a caller-supplied patch name (case-insensitive). NOT every dot — see
    /// <see cref="PatchStem"/>. Aliases the one shared home (<see cref="HousecarlCore.PluginFile.Extensions"/>) so this
    /// and the load-order reader / name-suggester copies can't diverge.</summary>
    static readonly string[] PluginExts = PluginFile.Extensions;

    /// <summary>Reduce a caller name to a safe bare STEM — no directory parts (so "../x" / "C:\y" can't escape ModsDir),
    /// stripping ONLY a trailing plugin extension (.esp/.esm/.esl), not every dot. A dotted patch name like
    /// "My.Cool.Patch" must survive intact: Path.GetFileNameWithoutExtension would clip it to "My.Cool" — and then an
    /// into="My.Cool.Patch" extend would look for the wrong folder, a silent name divergence. The plugin is always
    /// <c>&lt;stem&gt;.esp</c>; the mod folder is <c>houseCARL - &lt;stem&gt;</c>.</summary>
    static string PatchStem(string raw)
    {
        var name = Path.GetFileName(raw.Trim());
        foreach (var ext in PluginExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        return string.IsNullOrEmpty(name) ? "Patch" : name;
    }

    /// <summary>The given stem if it is free, else the first free "<c>&lt;stem&gt;_NNN</c>". Free means both that no
    /// mod folder "<c>houseCARL - &lt;stem&gt;</c>" already exists, houseCARL's own or a user's, and that no plugin
    /// "<c>&lt;stem&gt;.esp</c>" is already in the active load order. The load-order half stops a generic default
    /// stem from emitting a plugin that duplicates a foreign active one: the engine forbids two active plugins
    /// sharing a basename, and mod-folder uniqueness alone never sees a same-named plugin in another mod. into=
    /// remains the way to grow an existing patch; this is only the fresh path.
    /// <para>Neither test sees a plugin the active order is NOT loading, so the FILE the claiming lane will write is
    /// also put through <see cref="PatchStemShadow"/>, which REFUSES rather than suffixing (#561): a name suffixed
    /// around a foreign inactive plugin hides the file the caller may have meant. <paramref name="writes"/> is the
    /// calling lane's own statement of the file it emits and the parameter that names it, so the refusal never sends
    /// a caller to a parameter their tool does not declare; a lane that writes no plugin passes none and takes no
    /// shadow refusal. <paramref name="stemFromCaller"/> says the base stem is the caller's own name rather than the
    /// lane's default, which is what makes a shadow on it refusable.</para>
    /// <para><paramref name="refuseTaken"/> is the statement of a lane whose ARTIFACT takes this stem and whose exact
    /// basename is load-bearing — the merged plugin, the .bsa. A suffix there writes a file under a name the caller
    /// never asked for and nothing resolves, so those lanes REFUSE a taken stem by name, the way
    /// <c>create_plugin</c> does, instead of stepping to the next suffix. One resolver with a mode on it, not a
    /// second resolver, so every lane keeps the same derivation.</para></summary>
    string UniqueStem(string stem, bool stemFromCaller, PatchStemShadow.Target? writes, StemRefusal? refuseTaken = null)
    {
        var active = ActivePluginBasenames();
        // The sweep can only tell a shadow from the active plugin the suffix loop already dodges if it knows what the
        // order loads. With no composition, or no active plugin known at all, it does not run and the pre-existing
        // folder and active-order uniqueness stand — the same best-effort degradation both reads already document,
        // so a call that worked before never fails because the extra check could not run. A lane that writes no
        // plugin takes no shadow refusal either, so it does not pay the profile parse the sweep would need.
        var comp = active.Count == 0 || writes is null ? null : ReadCompositionForShadow();
        if (Takeable(stem)) return stem;
        for (int i = 1; i < 10000; i++)
        {
            var cand = $"{stem}_{i:D3}";
            if (Takeable(cand)) return cand;
        }
        throw new InvalidOperationException($"too many patches named '{stem}' under ModsDir — clean some out.");

        // Free of a folder and of an active plugin, and shadowing nothing. A shadow on the name the CALLER passed is
        // refused; a shadow on a suffix houseCARL invented is stepped past, since the caller cannot be told to avoid
        // a name they never chose, and the file follows the stem, so the next suffix clears it.
        bool Takeable(string s)
        {
            if (StemCollision(s, active) is { } taken)
            {
                // A lane whose artifact's basename is load-bearing refuses the name the CALLER passed rather than
                // writing that artifact under an invented suffix.
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

    /// <summary>The profile composition the shadow sweep walks, read once per fresh write, or null when the profile
    /// cannot tell a shadow from a loaded plugin. The test is whether the composition is USABLE, not whether the read
    /// threw: <see cref="Mo2LoadOrder.ReadComposition"/> does not throw on a missing modlist.txt, it returns empty mod
    /// lists — and then every folder under ModsDir reads as UNLISTED, so an enabled mod's genuinely loaded plugin
    /// would be refused as one "the load order is not loading". A profile naming no mod at all is that state, so it
    /// yields null. Best-effort in the same way <see cref="ActivePluginBasenames"/> is: null leaves the pre-existing
    /// folder and active-order uniqueness, so a call that worked before never fails because the check could not run.
    /// A modlist.txt truncated to a PARTIAL list is not detectable here and is not claimed to be.</summary>
    Mo2Composition? ReadCompositionForShadow()
    {
        try
        {
            var comp = Mo2LoadOrder.ReadComposition(_profileDir);
            // No mod named in either list — modlist.txt missing, or truncated to nothing — is an unusable profile.
            return comp.EnabledMods.Count == 0 && comp.DisabledMods.Count == 0 ? null : comp;
        }
        catch { return null; }
    }

    /// <summary>Null when a stem is free to claim — no houseCARL mod folder for it exists AND its plugin
    /// "<c>&lt;stem&gt;.esp</c>" isn't already an active load-order plugin (case-insensitive — Skyrim plugin basenames
    /// are) — else the sentence naming WHICH of the two is in the way, so a refusing lane can say it.</summary>
    string? StemCollision(string stem, IReadOnlySet<string> activePlugins)
        => Directory.Exists(Path.Combine(_modsDir, ModFolderName(stem)))
            ? $"a mod folder '{ModFolderName(stem)}' already exists"
            : activePlugins.Contains(stem + ".esp")
                ? $"a plugin named '{stem}.esp' is already active in your load order"
                : null;

    /// <summary>One lane's statement that its artifact's exact basename is load-bearing, so a taken stem refuses
    /// instead of auto-suffixing. <paramref name="Artifact"/> names the file in the refusal ("the merged plugin");
    /// <paramref name="Remedy"/> is that lane's own way out, since which parameters it declares differ.</summary>
    public readonly record struct StemRefusal(string Artifact, string Remedy);

    /// <summary>The active load order's plugin filenames, case-insensitive, for the UniqueStem collision check. Read
    /// from the already-built resolver if present, else the same cheap composition it builds from — deliberately not
    /// via the <see cref="Resolver"/> getter, which refuses a zero-plugin instance, a legitimate minimal write.
    /// Best-effort: any read failure, or no active plugins, yields an empty set and folder-only uniqueness, so the
    /// collision check is a safety net that never turns a previously-valid write into a failure.</summary>
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

    /// <summary>The four-step <c>into=</c> extend resolver, shared by the .esp write path
    /// (<see cref="ResolveOutputPath"/>) and the rider and asset path (<see cref="ResolvePatchModFolder"/>) so
    /// "extend my renamed patch" behaves identically across records, scripts, BSAs and assets. Resolves
    /// <paramref name="into"/> to the houseCARL-owned mod folder it names: the canonical "houseCARL - &lt;stem&gt;"
    /// fast path; then by the &lt;stem&gt;.esp it holds, for a renamed folder, since the .esp basename is fixed by
    /// whatever binds it; then by the folder's own name, since folder and plugin names need not match; then loud
    /// refusals naming every place searched, distinguishing a foreign un-owned collision from a genuine miss. Every
    /// step is ownership-gated, so a plugin houseCARL did not make stays refused — editing one is the separate
    /// in-place lane. <paramref name="needEsp"/> tightens the canonical fast path for the record lane, where the
    /// folder must actually hold &lt;stem&gt;.esp to short-circuit; the rider lane targets the folder itself. Caller
    /// holds <see cref="_gate"/>.
    /// <paramref name="freshPatch"/> is the calling operation's own statement about how, or whether, it can create a
    /// patch, and decides the fresh-write half of both refusals' remedy; <paramref name="noFreshRule"/> is the same
    /// statement from a lane the enum cannot express (removal, which creates nothing), and says WHY there is no
    /// fresh route. Both are deliberately separate from <paramref name="needEsp"/>. Each refusal is ONE sentence:
    /// what went wrong, then what to try, with the nearest owned patches named inside it (#359, #380).</summary>
    string ResolveOwnedPatchFolder(string into, bool needEsp,
                                   FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                                   RiderNaming? riderNaming = null)
    {
        var stem = PatchStem(into);                             // strips a trailing .esp/.esm/.esl; no directory parts (can't escape ModsDir)
        var espName = stem + ".esp";

        // Canonical fast path: "houseCARL - <stem>" still owns the patch, the common case, with no scan. The record
        // lane also requires it to hold <stem>.esp; the rider lane only needs the owned folder.
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

        // Folder catch-all: into= names the mod folder itself — the same-named-plugin disambiguator, and the way to
        // point at a renamed folder by its new name.
        var named = ResolveOwnedFolderByName(into);
        if (named is not null) return named;

        // Nothing matched. Distinguish a foreign, un-owned name collision — refused so originals stay untouched —
        // from a genuine miss, naming every place searched so the refusal reveals all the pieces at once.
        var bareName = Path.GetFileName(into.Trim());
        foreach (var cand in new[] { ModFolderName(stem), bareName })
        {
            var candPath = string.IsNullOrEmpty(cand) ? null : Path.Combine(_modsDir, cand);
            if (candPath is not null && Directory.Exists(candPath) && !IsHouseCarlOwned(candPath))
                // The fresh clause deliberately does not hand the caller their own stem back — a "<stem>.esp" minted
                // beside this folder's inactive "<stem>.esp" is two plugins that cannot both be active (#359). The
                // in-place lane, the other reading of a name that lands on a foreign folder, is NOT offered here:
                // this refusal is one sentence about the extend that failed, and the consent-gated lane stays
                // discoverable on the tool that declares it (Aaron, 2026-09-05).
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
        // The fresh-write remedy is the caller's to authorize: each operation states its OWN fresh-write path,
        // because it is not inferable here and the lane bit does not separate it. Three independent properties make
        // a shared assumption false. An operation may be unable to create a patch at all, editing an artifact that
        // must already exist, so a create remedy is false for it. It may create one but name it off something other
        // than the default — a caller-supplied identifier, or a stem its own call site fixes. Or the spelling may
        // name a DIFFERENT artifact on that operation, because it declares more than one output name. Which
        // operation is which is answered at the call sites, so a reader who wants the set greps the enum.
        // Hence the default claims no fresh-write path at all: a weaker "omit into= to create it fresh" is wrong for
        // any lane that cannot create anything, and telling such a caller to omit the lane sends them into a second
        // refusal. A caller added later without a thought about any of this gets the owned candidates alone, and
        // every stronger claim is one an operation makes for itself.
        // The candidates close every arm, because the likeliest cause of this refusal is a typo or an auto-suffixed
        // name, and the caller who needs them is exactly the one who reached it (#380).
        // The sentence deliberately does not predict the resulting filename. UniqueStem takes a stem only when it is
        // free on both tests — no "houseCARL - <stem>" folder exists, and no active plugin is named "<stem>.esp" —
        // and suffixes it otherwise, so the fresh clause qualifies the name it offers rather than promising a file.
        // A rider that named its own folder parameter answers with THAT parameter; the enum arms are the record
        // lanes', where the spelling is the same on every caller (#357).
        // It says to DROP into= because every lane here takes the extend branch on a non-blank into= and never looks
        // at the fresh name: adding the parameter to the call that just failed returns this same refusal — a loop on
        // the rider lanes, which declare no into=/patch= exclusivity check to intercept it.
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

    /// <summary>The owned patches this refusal may offer: the <c>into=</c> spellings it will name, nearest first and
    /// capped, how many further spellings the cap dropped, plus how many owned patches no single spelling reaches (the
    /// count is what the sentence says when there is nothing to name, since "houseCARL owns none" would send the caller
    /// off to mint a duplicate). <paramref name="ScanFailed"/> is the third way the list comes back empty — the scan
    /// itself threw — and it is not "houseCARL owns none" either.</summary>
    readonly record struct PatchCandidates(IReadOnlyList<string> Tokens, int BeyondCap, int Unreachable, bool NeedEsp,
                                           bool ScanFailed);

    /// <summary>One extend refusal, composed: what went wrong, then what to try, in ONE sentence with the nearest
    /// owned patches named inside it (Aaron, 2026-09-05). <paramref name="noFreshRule"/> is a lane's own statement
    /// that it has no fresh-write route and why, and <paramref name="fresh"/> is that route where a lane has one;
    /// each is omitted when the lane offers neither. No in-place clause rides here: that lane is discoverable on the
    /// tool that declares it.</summary>
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
        // A scan that threw is the third empty list, and the one thing that must NOT be said on it is that houseCARL
        // owns nothing: a user with forty patches would be sent to mint a duplicate beside them.
        else if (candidates.ScanFailed)
            tries.Add("again once the mods folder can be read, since houseCARL could not scan it just now and so cannot "
                    + "name a patch or tell whether it owns any");
        if (fresh is not null) tries.Add(fresh);
        // A lane with no fresh route, on an install owning nothing yet, otherwise stops dead — the likeliest first-time
        // path, and the dead end #359/#380 are about. There is nothing to extend, but there is something to say: the
        // patch has to exist before this lane can touch it, and only a writing lane can make one.
        // The tools are named from the constants, so a rename cannot leave this handing out a retired spelling.
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

    /// <summary>houseCARL-owned mod folders under ModsDir holding a plugin file named <paramref name="espFileName"/>
    /// at their root. The .esp basename is fixed — SPID files, config JSON and masters all bind the patch by its
    /// filename — while the MO2 mod-folder name is the user's to rename, so an extend finds the patch by the plugin
    /// it holds rather than the folder's current name. Ownership-gated by the marker, so a user mod that merely
    /// shares the basename is never returned. Returns full .esp paths.</summary>
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

    /// <summary>One owned patch folder as this refusal saw it: the plugins are read for EVERY owned folder, not just
    /// the rendered ones, because whether a token resolves depends on which OTHER folders hold the same plugin.</summary>
    readonly record struct OwnedPatch(string Dir, string Name, IReadOnlyList<string> Plugins);

    /// <summary>The houseCARL-owned patches an extend refusal may name as <c>into=</c> spellings, so it offers
    /// candidates instead of asking the caller to guess (#380). Every spelling emitted is one that RESOLVES back to
    /// the patch it stands for: each candidate token is run through this resolver's own three arms against the
    /// folders read here, and on the record lane it must also land on a definite plugin, so a caller who takes one
    /// literally never meets a second refusal. A patch no token reaches is counted instead, and the sentence says so
    /// when there is nothing to name. Nearest <paramref name="stem"/> first — the caller who reached this refusal
    /// typed something close to what they meant — ranked by the shared name-suggester and then, for the names it
    /// vouches for none of, by an edit distance that always answers, so the order is nearest on every path rather than
    /// the alphabet wherever the suggester declines. Capped at three so the refusal stays one readable sentence, with
    /// the drops counted so the named few never read as the whole inventory. <paramref name="needEsp"/> drops the
    /// folders holding no plugin, which the record lane could not extend anyway. Best-effort: an unreadable ModsDir
    /// yields no candidates — not a partial set, which could name a spelling the next call refuses — and the scan's
    /// failure is carried out so the refusal says that rather than that houseCARL owns nothing.</summary>
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
        // A throw partway through leaves a PARTIAL set, and reachability is decided by counting how many OTHER owned
        // folders hold the same plugin — so a dropped folder can make an ambiguous token look unambiguous and print a
        // spelling the next call refuses. No candidates is the documented degradation; half of them is a wrong answer.
        // The failure is carried out, because an empty list from a failed scan is not the same fact as an empty one.
        catch (IOException) { owned.Clear(); scanFailed = true; }
        catch (UnauthorizedAccessException) { owned.Clear(); scanFailed = true; }

        // Near-stem first (#380 asks for candidates near what the caller typed, not the alphabet's first eight).
        // Ranked by the same suggester the missed-plugin lookups use, over both the folder names and the plugin
        // names, so a typo'd folder and a typo'd plugin both float. The suggester answers EMPTY when nothing clears
        // its relevance bar, which left every folder tied and the list falling back to the alphabet while still being
        // called nearest — so the tie is broken by an edit distance that always answers, and the order is nearest
        // everywhere rather than only where the suggester vouches.
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
            // Nearest first inside a folder too: a folder reached only by its plugins offers several spellings, and
            // the cap means the one nearest what the caller typed is the one that has to survive.
            foreach (var t in tokens.OrderBy(NearRank).ThenBy(Distance))
                if (rows.Count < cap) rows.Add($"into=\"{t}\""); else beyondCap++;
        }
        // Two different facts reach an empty list, and only one of them is "there is nothing to extend". When every
        // owned patch was dropped because no single into= spelling reaches it, saying houseCARL owns none sends the
        // caller off to mint a fresh patch beside patches they could have extended — so the count is carried out.
        // The cap's own drops are counted too, so a named list never reads as the whole inventory. A scan that threw
        // is the third empty list, and it is not "there is nothing to extend" either.
        return new PatchCandidates(rows, beyondCap, unreachable, needEsp, scanFailed);
    }

    /// <summary>The <c>into=</c> spellings for one owned patch that this resolver actually routes back to it. The
    /// folder's own name first, since it is the most legible; where that name resolves elsewhere — another owned
    /// folder holds "&lt;folder name&gt;.esp", the by-plugin arm running before the folder catch-all — or where the
    /// folder holds several plugins and the record lane could not tell which to extend, the PLUGIN filenames stand in,
    /// one each. Bare names, quoted into <c>into=</c> by the caller. Empty when nothing reaches it.</summary>
    static List<string> ReachingTokens(List<OwnedPatch> owned, OwnedPatch target, bool needEsp)
    {
        var tokens = new List<string>();
        if (Reaches(target.Name)) tokens.Add(target.Name);
        else
            foreach (var p in target.Plugins)
                if (Reaches(p)) tokens.Add(p);
        return tokens;

        // The three resolution arms above, read off the folders already in hand rather than the disk: canonical
        // fast path, then the owned folder holding <stem>.esp, then the folder catch-all. A token also has to land
        // on ONE plugin on the record lane, which is what ResolveOutputPath does with the folder it gets back.
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

    /// <summary>A houseCARL-owned mod folder named exactly <paramref name="rawName"/> or
    /// "<c>houseCARL - &lt;rawName&gt;</c>" — the folder catch-all behind <c>into=</c>, where the user names the
    /// containing mod folder because the plugin basename is ambiguous or the folder was renamed. The folder name need
    /// not match the .esp inside. Bare name only, with no directory parts, so it cannot escape ModsDir. Null when no
    /// such folder is houseCARL-owned.</summary>
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

    /// <summary>The single top-level plugin in a houseCARL folder, so <c>into=</c> a folder name can edit "the plugin
    /// in this folder" without re-stating its basename. Null plus a named <paramref name="reason"/> when the folder
    /// holds none or more than one, rather than guessing which of several to extend.</summary>
    static string? SoleEspInFolder(string folder, out string reason)
    {
        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => PluginExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (plugins.Count == 1) { reason = ""; return plugins[0]; }
        reason = plugins.Count == 0
            ? "holds no plugin (.esp/.esm/.esl) to extend"
            : $"holds {plugins.Count} plugins ({string.Join(", ", plugins.Select(Path.GetFileName))}) — name the one to extend by passing its filename as into=";
        return null;
    }

    /// <summary>A mod folder is houseCARL-owned iff its <c>meta.ini</c> carries the <c>[houseCARL] generated=true</c>
    /// marker. The marker lives in meta.ini, the one mod-root file MO2 does not deploy into the game Data folder, so
    /// it never pollutes Data. Fail-safe: a missing or stripped marker reads as NOT owned, so houseCARL refuses to
    /// modify the folder rather than risk touching a user mod.</summary>
    static bool IsHouseCarlOwned(string folder) => HousecarlOwnerMeta.MarksOwned(folder);

    /// <summary>Write the new mod folder's <c>meta.ini</c>: the <c>[houseCARL]</c> ownership marker, which MO2 does
    /// not deploy, plus a minimal <c>[General]</c> for MO2's display. A minimal meta.ini is valid and the custom
    /// section is ours. A fresh folder has none, so this just writes it.</summary>
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

    /// <summary>Is a located file the copy the MO2 install serves for its filename, and if not, why not? One half of
    /// "does the game load this file"; the other is <see cref="TickStanding"/>. The two are independent — a copy can
    /// be both shadowed and unticked, each with its own remedy — so collapsing them to one cause would always drop
    /// one. NotAnInstallCopy is deliberately the zero value: a default-constructed result must read not-loaded.</summary>
    internal enum ServedStanding
    {
        /// <summary>The path is outside every install root, or no MO2 layer provides this exact file (a backup, an
        /// arbitrary path, or a copy reached through a junction the string compare can't match).</summary>
        NotAnInstallCopy = 0,
        /// <summary>THIS file is the copy the install serves — the first hit from an enabled layer.</summary>
        Serves,
        /// <summary>This copy's own layer is enabled, but a HIGHER-priority layer provides the same filename, so the
        /// game loads that one instead. Remedy: raise this mod's priority, or address the copy that wins.</summary>
        Shadowed,
        /// <summary>This copy sits in a mod folder MO2 knows about and has switched OFF. Remedy: switch it on, re-sort.</summary>
        ModDisabled,
        /// <summary>This copy sits in a folder modlist.txt does not mention at all, so MO2 has not registered it —
        /// the state of a patch houseCARL just wrote, before the refresh. Remedy: refresh MO2. Distinct from
        /// <see cref="ModDisabled"/> because "switch the mod on" is not available here: there is nothing in MO2's
        /// list to switch.</summary>
        ModUnregisteredLayer,
    }

    /// <summary>Is a plugin filename ticked to load — the other half of "does the game load this file". A plugin's
    /// tick state is a different fact from its mod folder's switch, MO2's right pane versus its left, which is the
    /// confusion this split exists to end. Unregistered is the zero value for the same conservative reason as in
    /// <see cref="ServedStanding"/>.</summary>
    internal enum TickStanding
    {
        /// <summary>plugins.txt and loadorder.txt do not mention this filename at all — MO2 has not registered it.</summary>
        Unregistered = 0,
        /// <summary>`*`-prefixed in plugins.txt — checked.</summary>
        Ticked,
        /// <summary>A base-game/CC master: force-loaded and never listed in plugins.txt, so absence there means loaded,
        /// not unloaded.</summary>
        Implicit,
        /// <summary>Listed in plugins.txt WITHOUT the `*` — present but unchecked. The game does not load it.</summary>
        Unticked,
    }

    /// <summary>One located plugin file, or why not. Exactly one of Path, Ambiguous or Error is set.
    /// <para>The two standings are carried separately rather than pre-collapsed into one boolean, so a renderer can
    /// explain rather than merely classify: "not active" names the state but not the cause, and the causes —
    /// unticked, mod switched off, shadowed, unregistered — have different remedies. <see cref="Enabled"/> keeps the
    /// single "the game loads this file" boolean, derived rather than stored.</para></summary>
    /// <param name="CauseDetail">For <see cref="ServedStanding.Shadowed"/>, the where-label of the copy that IS
    /// served, which is a different copy so it never collides with <paramref name="Where"/>. For the two layer-off
    /// standings, the mod FOLDER NAME alone — never the full hit label, whose text varies per lane and carries its
    /// own remedy, which makes the composed sentence say the same thing twice.</param>
    /// <param name="WhereNamesLayer">Does <paramref name="Where"/> already identify which layer holds this copy? Set
    /// by each lane from what it knows: the filename lane's Where IS the hit's label and the mod= lane's names the
    /// mod, while the direct-path lane's is a constant that identifies nothing. Carried as a fact rather than
    /// re-derived by string-comparing the two labels, because that comparison holds for one lane and silently fails
    /// for another whose label omits the state qualifier.</param>
    internal readonly record struct PluginLocateResult(
        string? Path, string Where, ServedStanding Served, TickStanding Tick, string? CauseDetail,
        bool WhereNamesLayer,
        IReadOnlyList<PluginFileHit>? Ambiguous, string? Error)
    {
        /// <summary>The game loads THIS file: it is the served copy AND its plugin is ticked (implicit masters count —
        /// force-loaded, never listed). Both halves, or the same physical file answers differently depending on how it
        /// was addressed.</summary>
        public bool Enabled => Served == ServedStanding.Serves && Tick is TickStanding.Ticked or TickStanding.Implicit;

        /// <summary>Why the game does not load this file — null when <see cref="Enabled"/>, and also when no file was
        /// located at all. Composed here, once, so every renderer that states it cannot drift apart on the wording.
        /// Both clauses are emitted when both apply: a shadowed copy of an unticked plugin needs two fixes, and
        /// naming one would send the reader to do half the job. The unregistered clause is suppressed when the served
        /// half already failed, because a disabled mod already explains the absence from plugins.txt and repeating it
        /// reads as a second problem.</summary>
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
                        // CauseDetail is always set here: Shadowed is returned only when this copy's own layer is
                        // enabled, which means a served hit exists to name. That hit is a different copy, so naming
                        // it never duplicates Where whichever lane asked.
                        parts.Add($"this copy is SHADOWED — {CauseDetail} provides the copy the game loads");
                        break;
                    // The two layer-off standings name the folder only when Where does not, and state the layer's
                    // condition and remedy in words rather than echoing a label, which is what got printed twice.
                    // Their remedies genuinely differ, which is why they are separate standings: an unregistered
                    // folder has nothing in MO2's list to switch on.
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
                        // States what was CHECKED, not a verdict on the file. This arm is also reached when the path
                        // string-compares miss — a junction, a subst drive, a UNC route to the same install — where
                        // "not a copy the install provides" would be a confident sentence that is simply false.
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

    /// <summary>Judge the served half for one located file: is <paramref name="fullPath"/> the copy the install
    /// provides for its filename, and if not, which of the three not-served states is it? Judged against the first
    /// hit from an ENABLED layer, which is the rule the real order is built by — not merely the first hit, because
    /// the locate also walks disabled and unlisted folders the order never consults. Compared by full path, since a
    /// backup and the live copy share a filename and are different files.</summary>
    static (ServedStanding Served, string? Detail) JudgeServed(
        Mo2Composition comp, IReadOnlyList<PluginFileHit> located, string fullPath)
    {
        var served = located.FirstOrDefault(h => h.Enabled);
        if (served is not null && SamePluginFile(served.Path, fullPath)) return (ServedStanding.Serves, null);
        var own = located.FirstOrDefault(h => SamePluginFile(h.Path, fullPath));
        if (own is null) return (ServedStanding.NotAnInstallCopy, null);          // outside the install, or unreachable by string compare
        // Its own layer is ON but something else serves the name ⇒ shadowed, and the useful pointer is the copy that
        // WINS, not this one.
        if (own.Enabled) return (ServedStanding.Shadowed, served?.Where);
        // Its own layer is off. WHICH kind decides the remedy, and it is read from the profile's own mod list rather
        // than by pattern-matching the hit's label text — the label is display prose that can be reworded, while
        // modlist.txt membership is the actual fact ("switched off" vs "never registered").
        var folder = Path.GetFileName(Path.GetDirectoryName(own.Path) ?? "") ?? "";
        bool listedOff = comp.DisabledMods.Any(m => m.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return (listedOff ? ServedStanding.ModDisabled : ServedStanding.ModUnregisteredLayer, folder);
    }

    /// <summary>Judge the tick half for one plugin filename, from the profile text files. Kept beside
    /// <see cref="JudgeServed"/> so the two halves can never be computed by different rules in different
    /// lanes.</summary>
    static TickStanding JudgeTick(Mo2Composition comp, string fileName)
    {
        if (comp.ActivePluginNames.Contains(fileName)) return TickStanding.Ticked;
        foreach (var x in comp.ImplicitPluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Implicit;
        foreach (var x in comp.InactivePluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Unticked;
        return TickStanding.Unregistered;
    }

    /// <summary>The on-disk plugin-locate contract, shared by every lane that resolves a plugin by name, so no two
    /// can diverge. A direct path — rooted or carrying a separator — is used verbatim, so any plugin file can be
    /// inspected; otherwise the argument is a filename found across the whole install (enabled and disabled mod
    /// folders, overwrite, Data), with <paramref name="mod"/> narrowing a name several folders provide. Ambiguity
    /// comes back structured, so each caller renders its own remedy.
    /// <para><paramref name="offerModParam"/> controls whether the not-found refusal offers <c>mod=</c> as the
    /// disambiguator, which is only true for callers that have that parameter: a refusal must never send someone to
    /// a parameter their tool does not expose.</para></summary>
    internal static PluginLocateResult LocatePluginFileOnDisk(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string plugin, string? mod,
        bool offerModParam = true)
    {
        // A plugin's tick state is a different fact from its mod folder's switch: a plugin can sit in an enabled mod
        // and be unchecked in MO2's right pane, and the game then does not load it. Every lane below returns the
        // (served, tick) pair the renderers state as active or not-active-because, so both halves are judged in
        // every lane by the same two helpers; a lane computing one its own way is how they diverge. Implicit base
        // and CC masters are force-loaded and never listed in plugins.txt, so they count as ticked.
        if (LooksLikePath(plugin))
        {
            if (!File.Exists(plugin))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null, $"no file at path '{plugin}'.");
            var full = Path.GetFullPath(plugin);
            // The standing is computed for a direct path, never assumed: addressing a file by path says nothing about
            // whether the install provides it, and a path can perfectly well name the live copy of an enabled plugin.
            // JudgeServed answers whether THIS file is the copy the install serves, against the first enabled-layer
            // hit. Two costs are accepted: this pays the same folder sweep the filename lane does, which is why a
            // path inside no install root skips it outright; and a path reaching the install through a junction or
            // symlink will not string-match, so it reads as NotAnInstallCopy — conservative rather than wrong in the
            // other direction. The tick half needs no path at all, so it is judged for every direct path.
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
            // Both halves here too, or the same physical file answers differently depending on how it was addressed.
            // "The named mod is enabled" is NOT enough: a lower-priority enabled mod's copy is shadowed, and the game
            // loads the serving copy instead.
            var (modServed, modDetail) = JudgeServed(
                comp, Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fn), cand);
            // WhereNamesLayer is true: "mod 'X'" names the folder, though it carries no state qualifier — which is
            // why a label-equality test would fail here and let the duplication through.
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

    /// <summary>Does the user's `plugin` argument denote a PATH (use verbatim — the "inspect any file" case) rather
    /// than a bare filename (locate in the MO2 folders)? True if rooted or carrying a directory separator: 'C:\..\X.esp'
    /// or 'mods\M\X.esp' is a path; a bare 'X.esp' is a filename.</summary>
    static bool LooksLikePath(string s) => Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/');

    /// <summary>Do two paths denote the same plugin file? A full-path, case-insensitive compare, never a filename
    /// compare: an archived backup and the live copy share a name and are different files.</summary>
    static bool SamePluginFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Is <paramref name="fullPath"/> inside any MO2 or game root? Used only to skip work, since a file
    /// outside every root cannot be a copy the install provides, so the enabled/disabled classification itself stays
    /// with the shared locate and is never re-derived here.</summary>
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

    // ---- corpus-backed type resolution (signature "WEAP" / catalog name "Weapon" → getter Type(s)) -------

    Dictionary<string, List<Type>>? _typeLookup;
    Dictionary<string, List<Type>> TypeLookup => _typeLookup ??= BuildTypeLookup();

    /// <summary>Build the type-string to getter-Type map from the corpus, the authoritative type catalog. Keyed by
    /// both catalog name and 4-char signature; a many-to-one signature accumulates its variants so a signature query
    /// unions them. An abstract-group base name maps to its concrete arms' getter Types by construction — the same
    /// union the signature yields — so a query by the base name unions them too, and the callers' ambiguity branch
    /// names the variants. A corpus type name that will not load is skipped here and surfaces as "unknown type" at
    /// query time, never as a silently wrong type.</summary>
    static Dictionary<string, List<Type>> BuildTypeLookup()
    {
        var lookup = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, Type t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<Type>();
            if (!list.Contains(t)) list.Add(t);
        }
        var corpus = CorpusRulebook.LoadCorpus();
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "record") continue;
            var t = Type.GetType(ts.GetterInterfaceAssemblyQualified);
            if (t is null) continue;
            Add(ts.Name, t);
            Add(ts.Signature, t);
        }
        // Abstract-group base names map to their concrete arms' getter Types. The arms are listed on the
        // polymorphic-base's own corpus entry, so the union is derived rather than hand-wired — the generated-coverage
        // cornerstone — and a query by the base name resolves to the same set its signature does.
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "polymorphic-base" || ts.Arms is not { Count: > 0 } arms) continue;
            foreach (var armName in arms)
                if (corpus.Types.TryGetValue(armName, out var arm) && arm.Kind == "record"
                    && Type.GetType(arm.GetterInterfaceAssemblyQualified) is { } at)
                    Add(ts.Name, at);
        }
        return lookup;
    }

    /// <summary>A user type SET to its getter Types: the union of each entry's resolution, in order, deduped — one
    /// grammar with the singular form, since every entry goes through <see cref="ResolveTypeFilter"/> and an unknown
    /// one throws naming itself. Null for an absent or empty set, so the unnarrowed path stays untouched.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types) => ResolveTypeFilterSet(types, out _);

    /// <summary>The display names a type SET resolves to — the same spelling a matched body's type renders as, so a
    /// caller can seat a requested type in a count table whether or not any record landed in it. Null for an absent
    /// set, and null for an unknown entry too: the surfaces that call this have already refused one by name, and a
    /// census is not the place to raise it a second time.</summary>
    public IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<string>? types)
    {
        try { return TypeDisplayNames(ResolveTypeFilterSet(types)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Seat every type the scan NAMED in a group_by=type census at zero, before a single match is counted.
    /// A requested type with no records then reads as a 0 row rather than being absent from the table, which a
    /// caller could only read by diffing the request against the response. No-op for the other count keys and for a
    /// scan that named no types.</summary>
    static void SeedRequestedTypes(Dictionary<string, int>? groups, string? groupBy, IReadOnlyList<Type>? types)
    {
        if (groups is null || groupBy != "type") return;
        foreach (var name in TypeDisplayNames(types) ?? Array.Empty<string>()) groups.TryAdd(name, 0);
    }

    /// <summary>The same names off the already-resolved getter Types.</summary>
    static IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<Type>? types) =>
        types is { Count: > 0 } ? types.Select(t => RecordNaming.StripGetterInterface(t.Name)).Distinct(StringComparer.Ordinal).ToList() : null;

    /// <summary>The same resolution, also spelling each entry with the arms it expanded to
    /// (<see cref="SweepScope.SpellTypeEntry"/>) — the label a response needs when it has to name the types that
    /// share one listing, since an entry like <c>GMST</c> names none of them by itself.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types, out string? armLabel)
    {
        armLabel = null;
        if (types is not { Count: > 0 }) return null;
        var union = new List<Type>();
        var spelled = new List<string>(types.Count);
        foreach (var ts in types)
        {
            var entry = (ts ?? "").Trim();
            var arms = ResolveTypeFilter(entry);
            foreach (var t in arms)
                if (!union.Contains(t)) union.Add(t);
            spelled.Add(SweepScope.SpellTypeEntry(entry, arms));
        }
        armLabel = string.Join(", ", spelled);
        return union;
    }

    /// <summary>A user type string to its getter Types. Throws, naming the bad input and what is expected.
    /// <para>A BLANK entry is refused as blank, in this one place, so every surface that takes a type set — the
    /// records surface and the check sweep alike — states the same rule: an entry that names no type is refused
    /// saying it is blank, never quoted back as an unknown type <c>''</c>. An empty or absent SET is a different
    /// thing (no narrowing) and is handled by the callers.</para></summary>
    IReadOnlyList<Type> ResolveTypeFilter(string type)
    {
        if (type.Trim().Length == 0)
            throw new ArgumentException(
                "a blank record type — pass a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon'), " +
                "or omit the parameter to leave the types unnarrowed.");
        if (TypeLookup.TryGetValue(type.Trim(), out var types)) return types;
        throw new ArgumentException(
            $"unknown record type '{type}'. Expected a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon').");
    }

    /// <summary>A form-scope string to getter Types: a catalog name or signature via the type lookup, or a Mutagen
    /// link-interface group name such as "Item" or "Constructible", resolved as every corpus record getter assignable
    /// to <c>I{name}Getter</c> — derived from the real interfaces, never a hand-kept list. The SkyPatcher field map
    /// scopes multi-type ops by these group names, so the plain type lookup alone cannot serve it. Null means the
    /// string names neither, and the caller surfaces that loudly.</summary>
    internal IReadOnlyList<Type>? ResolveFormScope(string type)
    {
        var t = type.Trim();
        if (TypeLookup.TryGetValue(t, out var types)) return types;
        var iface = typeof(SkyrimMod).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{t}Getter");
        if (iface is null) return null;
        var matches = TypeLookup.Values.SelectMany(v => v).Distinct().Where(iface.IsAssignableFrom).ToList();
        return matches.Count > 0 ? matches : null;
    }

    public void Dispose()
    {
        lock (_gate) { _resolver?.Dispose(); _resolver = null; _assetResolver?.Dispose(); _assetResolver = null; }
    }
}

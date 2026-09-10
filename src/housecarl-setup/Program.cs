using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>
/// houseCARL desktop setup - a no-CLI, no-GUI double-click installer.
///
/// houseCARL can be hosted by TWO agents; this utility installs for either or both, behind a
/// pick-a-number prompt (or a --claude / --codex / --both flag for an unattended run):
///
///   [1] Claude Code - copies the bundled plugin into ~/.claude/skills/housecarl/ (the desktop app
///                     auto-loads its skills) and registers the MCP server in ~/.claude.json (the
///                     desktop spawns it per session). UNCHANGED from the proven desktop install.
///   [2] Codex       - installs the server under %LOCALAPPDATA%\houseCARL\server\, copies the helper
///                     skills + the houseCARL umbrella skill FLAT into ~/.agents/skills/ (the location
///                     a fresh Codex install was confirmed to scan), and registers the server as
///                     [mcp_servers.housecarl] in ~/.codex/config.toml.
///   [3] Both        - both of the above.
///   [4] Uninstall   - takes back what an install wrote, for either host or both. It is its own file,
///                     <see cref="Uninstall"/>, which says exactly what comes off per host.
///
/// The MO2 folder is intentionally NOT set here; houseCARL asks for it in chat on first use and stores
/// it in user.json beside whichever server copy is running.
///
/// Codex layout note: Codex scans ~/.agents/skills/ for skill FOLDERS, so the skills go there flat (not
/// nested inside a plugin folder), and the server - which is not a skill - lives in its own neutral dir.
/// For a Both install each host runs its own server copy (so MO2 is set once per host); unifying to a
/// single shared server is a deferred clean-up that would re-touch the proven Claude path.
/// </summary>
public static class Program
{
    private const string PluginFolderName = "housecarl"; // plugin dir shipped beside this exe
    private const string McpServerName    = "housecarl"; // server key under mcpServers / [mcp_servers.*]

    // Major version of the runtimes the bundled SERVER needs (keep in sync with housecarl-mcp's
    // TargetFramework). The default roll-forward policy stays within a major, so "10.x installed"
    // does not satisfy a net9.0 framework-dependent server.
    private const string ServerRuntimeMajor = "9";

    public enum Target { Claude, Codex, Both }

    /// <summary>What a non-interactive <see cref="TryInstall"/> did.</summary>
    public enum InstallOutcome
    {
        /// <summary>Skills + server copied and the MCP server registered for the chosen host(s).</summary>
        Installed,
        /// <summary>A houseCARL server file at a destination is in use (a live Claude/Codex session is
        /// running it), so it could not be overwritten. Nothing usable was changed when the refusal was at
        /// pre-flight (<see cref="InstallResult.RefusedBeforeAnyCopy"/>).</summary>
        ServerInUse,
    }

    /// <summary>Result of a non-interactive install attempt — the probeable seam under the interactive prompt.</summary>
    /// <param name="Outcome">What happened.</param>
    /// <param name="Message">Caller-facing detail for a non-<see cref="InstallOutcome.Installed"/> outcome (else null).</param>
    public sealed record InstallResult(InstallOutcome Outcome, string? Message)
    {
        /// <summary>True only when a <see cref="InstallOutcome.ServerInUse"/> refusal happened at PRE-FLIGHT,
        /// before any file was copied (so nothing was changed). False for the mid-copy defense-in-depth catch.</summary>
        public bool RefusedBeforeAnyCopy { get; init; }
    }

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("houseCARL setup - installs houseCARL into Claude Code and/or Codex.");
            Console.WriteLine();
            Console.WriteLine("  Just run it (double-click) and pick which host(s) to install for.");
            Console.WriteLine("  Or pass a flag to skip the prompt:");
            Console.WriteLine("    --claude   install for Claude Code only");
            Console.WriteLine("    --codex    install for Codex only");
            Console.WriteLine("    --both     install for both");
            Console.WriteLine("    --yes      skip the confirm at the plan (it still asks which host,");
            Console.WriteLine("               so an unattended run needs a host flag as well)");
            Console.WriteLine("    --uninstall   remove houseCARL instead of installing it, from the host");
            Console.WriteLine("                  named by --claude/--codex/--both");
            Console.WriteLine("    --skip-runtime-check   skip the .NET runtime preflight (custom DOTNET_ROOT etc.)");
            Console.WriteLine();
            Console.WriteLine("  Setup asks two questions - which host, and the confirm at the plan - and it");
            Console.WriteLine("  never answers one for you. A run whose input is redirected has nobody to ask,");
            Console.WriteLine("  so it refuses and names the flag it needed: --claude/--codex/--both for the");
            Console.WriteLine("  host, --yes for the confirm.");
            return 0;
        }

        try
        {
            Ui.Banner(SetupVersion());

            // Locate the plugin shipped beside this program.
            string pkgDir      = AppContext.BaseDirectory;
            string pluginSrc   = Path.Combine(pkgDir, PluginFolderName);
            string srcManifest = Path.Combine(pluginSrc, ".claude-plugin", "plugin.json");
            string srcExe      = Path.Combine(pluginSrc, "server", "housecarl-mcp.exe");
            // The skills folder is checked with the manifest and the exe: a package always ships one, and a
            // package missing it is a broken unzip, not a version that dropped every skill.
            string srcSkills   = Path.Combine(pluginSrc, "skills");

            // HOUSECARL_SETUP_HOME overrides the home dir (testing / unusual setups).
            string? homeOverride = Environment.GetEnvironmentVariable("HOUSECARL_SETUP_HOME");
            string home = string.IsNullOrWhiteSpace(homeOverride)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeOverride;

            // ---- detection ---------------------------------------------------
            // Read the machine before writing to it: which hosts are here, what houseCARL each already has,
            // and the runtimes the bundled server needs. Reading only, so a run cancelled at the plan below
            // leaves everything as it found it.
            //
            // The bundled server is framework-dependent net9.0 + ASP.NET Core: it needs BOTH the
            // base .NET Runtime (Microsoft.NETCore.App) and the ASP.NET Core Runtime
            // (Microsoft.AspNetCore.App). On Windows those are TWO separate installers, and the
            // ASP.NET Core one does NOT include the base runtime -- a real-world install trap.
            // This exe ships self-contained precisely so it still runs on a machine with neither
            // and can say exactly what's missing, instead of the install "succeeding" into a
            // server that never starts.
            bool skipRuntimeCheck = args.Contains("--skip-runtime-check");
            List<string> missingRuntimes = skipRuntimeCheck ? new List<string>() : MissingServerRuntimes();
            Detect.HostState claude = Detect.Claude(home);
            Detect.HostState codex  = Detect.Codex(home, homeOverride);
            ReportDetected(claude, codex, missingRuntimes, skipRuntimeCheck);

            Answer chose = ResolveChoice(args, claude, codex, out Mode mode, out Target target);
            if (chose == Answer.NoOneToAsk) return Finish(1);
            if (chose == Answer.Quit)
            {
                Console.WriteLine("Cancelled - nothing was changed.");
                return Finish(0);
            }

            // The runtimes are the SERVER's, so only an install needs them. The refusal is therefore made after
            // the choice rather than before it: a machine missing a runtime is exactly a machine somebody may
            // want to remove houseCARL from, and refusing at the detection block would leave no way to.
            if (mode == Mode.Install && missingRuntimes.Count > 0)
            {
                ReportMissingRuntimes(missingRuntimes);
                return Finish(1);
            }

            // The package is the install's, for the same reason and after the same choice: a removal reads
            // nothing out of it — what it takes off the machine is what the install put there — and refusing
            // before the menu would leave a broken or half-unzipped package with no way to reach [4] Uninstall.
            if (mode == Mode.Install
                && (!File.Exists(srcManifest) || !File.Exists(srcExe) || !Directory.Exists(srcSkills)))
            {
                ReportBrokenPackage(pluginSrc, srcManifest, srcExe, srcSkills);
                return Finish(1);
            }

            // ---- the plan, before anything is written ------------------------
            string version = Detect.PluginVersion(srcManifest) ?? SetupVersion();
            List<Plan.HostPlan> plans = mode == Mode.Install
                ? Plan.For(target, home, homeOverride, claude, codex)
                : Plan.ForRemoval(target, home, homeOverride, claude, codex);
            Plan.Print(plans, version, removing: mode == Mode.Uninstall);
            Answer confirmed = Confirm(args, mode);
            if (confirmed == Answer.NoOneToAsk) return Finish(1);
            if (confirmed == Answer.Quit)
            {
                Console.WriteLine(mode == Mode.Install
                    ? "Cancelled - nothing was installed."
                    : "Cancelled - nothing was removed.");
                return Finish(0);
            }

            Console.WriteLine();

            if (mode == Mode.Uninstall)
            {
                Uninstall.Result removal = Uninstall.TryUninstall(target, home, homeOverride);
                if (removal.What == Uninstall.Outcome.ServerInUse)
                {
                    ReportServerInUse(removal.Message, removal.RefusedBeforeAnyDelete
                        ? "A houseCARL server file is in use, so setup stopped before removing anything — fully "
                          + "quit Claude Code and Codex, then run this setup again."
                        : "A houseCARL file went into use partway through the removal, so setup stopped — fully "
                          + "quit Claude Code and Codex, then run this setup again to finish it.");
                    return Finish(1);
                }
                Plan.PrintRemovalSummary(removal.Hosts);
                return Finish(0);
            }

            InstallResult result = TryInstall(target, pluginSrc, home, homeOverride);
            if (result.Outcome == InstallOutcome.ServerInUse)
            {
                ReportServerInUse(result.Message, result.RefusedBeforeAnyCopy
                    ? "A houseCARL server file is in use, so setup stopped before changing anything — fully quit "
                      + "Claude Code and Codex, then run this setup again."
                    : "A houseCARL file went into use partway through the update, so setup stopped — fully quit "
                      + "Claude Code and Codex, then run this setup again to finish it.");
                return Finish(1);
            }

            Plan.PrintSummary(plans, version);
            return Finish(0);
        }
        catch (Exception ex)
        {
            Ui.Problem(
                "houseCARL setup hit an error it does not have a fix for and stopped before finishing — the "
                + "line below is what the failure reported, and the steps above it are how far it got.",
                ex.Message);
            return Finish(1);
        }
    }

    /// <summary>The detection block: one row per thing setup looked for, printed before any choice is offered.</summary>
    private static void ReportDetected(
        Detect.HostState claude, Detect.HostState codex, List<string> missingRuntimes, bool skipped)
    {
        Ui.Heading("Checking your machine");
        string baseName = ".NET Runtime " + ServerRuntimeMajor;
        string aspName  = "ASP.NET Core Runtime " + ServerRuntimeMajor;
        Ui.Row(baseName, RuntimeStatus(skipped, missingRuntimes.Contains("Microsoft.NETCore.App")));
        Ui.Row(aspName,  RuntimeStatus(skipped, missingRuntimes.Contains("Microsoft.AspNetCore.App")));
        Ui.Row(claude.Name, claude.Summary);
        Ui.Row(codex.Name,  codex.Summary);
    }

    /// <summary>A runtime row's status: a runtime the install needs and cannot find is the one thing in this
    /// block that stops it, so it reads as a problem; one that was not looked at reads plain.</summary>
    private static Ui.StatusWord RuntimeStatus(bool skipped, bool missing)
        => skipped ? Ui.StatusWord.Plain("not checked (--skip-runtime-check)")
         : missing ? Ui.StatusWord.Missing("not found")
         :           Ui.StatusWord.Good("found");

    /// <summary>What came back from a question setup had to ask a person.</summary>
    private enum Answer
    {
        /// <summary>They answered it: go on.</summary>
        Yes,
        /// <summary>They said no: stop, having changed nothing.</summary>
        Quit,
        /// <summary>There was nobody to ask (redirected input) and no flag that answered it in advance. The
        /// refusal has already been printed; the run stops.</summary>
        NoOneToAsk,
    }

    /// <summary>
    /// The plan's confirm. It stands on every attended run, including one started with a host flag, because the
    /// paths are the thing worth reading before they are written. <c>--yes</c> is the one way past it. A run
    /// whose input is redirected has nobody to press a key, so without that flag it is refused rather than read
    /// as a yes: the same rule as the host menu, which is the other question setup asks.
    /// </summary>
    private static Answer Confirm(string[] args, Mode mode)
    {
        if (args.Contains("--yes")) return Answer.Yes;
        string go = mode == Mode.Install ? "install" : "remove";
        while (true)
        {
            string? s = Console.IsInputRedirected
                ? null
                : Ui.Prompt("  Press Enter to " + go + ", or q to quit.  > ");
            if (s is null)
            {
                Ui.Problem(
                    "Setup cannot ask you to confirm the plan above, because its input is redirected and there "
                    + "is nobody to press a key — re-run with --yes to " + go + " without stopping at the plan.",
                    "Nothing was changed.");
                return Answer.NoOneToAsk;
            }
            string answer = s.Trim().ToLowerInvariant();
            if (answer.Length == 0) return Answer.Yes;
            if (answer is "q" or "quit") return Answer.Quit;
            Console.WriteLine("  Press Enter to " + go + ", or type q to quit.");
        }
    }

    /// <summary>
    /// The runtime refusal, sentence-first and naming only the runtime that is actually missing. The
    /// two-installer trap is the hard-won part and stays, but below the sentence: the ASP.NET Core installer
    /// does NOT carry the base .NET Runtime, so a machine can have one and not the other.
    /// </summary>
    /// <summary>Said when the package beside this exe cannot be installed from: either the plugin folder is not
    /// there at all, or it is there and missing a piece, which is a partial unzip and names the piece.</summary>
    private static void ReportBrokenPackage(string pluginSrc, string srcManifest, string srcExe, string srcSkills)
    {
        if (!Directory.Exists(pluginSrc))
        {
            Ui.Problem(
                "The houseCARL plugin folder is not next to this program, so there is nothing to "
                + "install — keep this program beside the unzipped 'housecarl' folder and run it again.",
                "Looked in:  " + pluginSrc);
            return;
        }

        // The folder is there, so this is an incomplete unzip: name the piece that is missing.
        string missingWhat = !File.Exists(srcManifest) ? "manifest"
                           : !File.Exists(srcExe)      ? "server"
                           :                             "skills folder";
        string missingPath = !File.Exists(srcManifest) ? srcManifest
                           : !File.Exists(srcExe)      ? srcExe
                           :                             srcSkills;
        Ui.Problem(
            "The houseCARL plugin folder beside this program is missing its " + missingWhat
            + ", so the download did not unzip completely — unzip it again and run this setup.",
            "Looked in:  " + pluginSrc,
            "Missing:    " + missingPath);
    }

    private static void ReportMissingRuntimes(List<string> missing)
    {
        bool baseMissing = missing.Contains("Microsoft.NETCore.App");
        bool aspMissing  = missing.Contains("Microsoft.AspNetCore.App");
        string dotnetName = ".NET Runtime " + ServerRuntimeMajor;
        string aspName    = "ASP.NET Core Runtime " + ServerRuntimeMajor;

        string sentence = baseMissing && aspMissing
            ? "houseCARL needs the " + dotnetName + " and the " + aspName + ", and neither is installed on this "
              + "machine — install them and run this setup again."
            : "houseCARL needs the " + (baseMissing ? dotnetName : aspName) + ", which is not installed on this "
              + "machine — install it and run this setup again.";

        List<string> detail = new()
        {
            "https://dotnet.microsoft.com/download/dotnet/" + ServerRuntimeMajor + ".0",
        };
        if (baseMissing) detail.Add("or:  winget install Microsoft.DotNet.Runtime." + ServerRuntimeMajor);
        if (aspMissing)  detail.Add("or:  winget install Microsoft.DotNet.AspNetCore." + ServerRuntimeMajor);
        detail.Add("");

        if (baseMissing && aspMissing)
        {
            detail.Add("They are two separate installers, and the ASP.NET Core one does not include");
            detail.Add("the base .NET Runtime, so you need both.");
        }
        else
        {
            detail.Add("The " + (baseMissing ? aspName : dotnetName) + " is already here. They are two separate");
            detail.Add("installers and the ASP.NET Core one does not include the base .NET Runtime,");
            detail.Add("so this is the only piece missing.");
        }

        detail.Add("");
        detail.Add("(Custom dotnet location? Re-run with --skip-runtime-check.)");

        Ui.Problem(sentence, detail.ToArray());
    }

    // ---- non-interactive install (the probeable seam under the prompt) -----

    /// <summary>
    /// Copy the plugin + register the MCP server for the chosen host(s), non-interactively. This is the seam
    /// <see cref="Main"/> drives after the prompt, and the one the CI guard drives directly.
    ///
    /// Re-running setup over a LIVE install would have <see cref="CopyDirectory"/> overwrite the running
    /// <c>housecarl-mcp.exe</c> (File.Copy overwrite:true), throw mid-copy, and leave a half-updated tree
    /// behind a generic "did not complete". So we PRE-FLIGHT the lock at EVERY destination this target
    /// touches, before copying anything, and refuse with actionable guidance (mirrors the runtime preflight
    /// below). A clean first install (no destination exe yet) is never blocked. As defense in depth, a
    /// sharing violation that slips past the pre-flight (a held sibling DLL, or a session started between the
    /// check and the copy) is caught and surfaced with the same guidance instead of the generic failure.
    ///
    /// The stale-skill prune runs between the copy and the registration, so a folder it cannot delete is
    /// collected rather than thrown: cleanup does not fail an install that otherwise worked, and the run says
    /// at the end which folders are still there.
    /// </summary>
    public static InstallResult TryInstall(Target target, string pluginSrc, string home, string? homeOverride)
    {
        List<string> destExes = new();
        if (target is Target.Claude or Target.Both) destExes.Add(ClaudeDestExe(home));
        if (target is Target.Codex  or Target.Both) destExes.Add(CodexDestExe(home, homeOverride));

        foreach (string destExe in destExes)
            if (ServerExeInUse(destExe))
                return new InstallResult(InstallOutcome.ServerInUse,
                        "In use, or locked / read-only:  " + destExe)
                    { RefusedBeforeAnyCopy = true };

        // Leftover skill folders the prune could not delete. Cleanup never fails an otherwise good install, so
        // the failures are collected and said at the end instead of thrown.
        List<string> keptBack = new();

        try
        {
            if (target is Target.Claude or Target.Both) InstallForClaude(pluginSrc, home, keptBack);
            if (target is Target.Codex  or Target.Both) InstallForCodex(pluginSrc, home, homeOverride, keptBack);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // The try wraps the copy AND the host-config registration, so the locked file may be the server
            // exe/DLL or a config file (e.g. ~/.codex/config.toml open in an editor) — name both honestly.
            return new InstallResult(InstallOutcome.ServerInUse,
                "The file was the server, or a config file setup writes.");
        }

        ReportKeptBack(keptBack);
        return new InstallResult(InstallOutcome.Installed, null);
    }

    // ---- destination paths (one source of truth for pre-flight, plan and installer) ----

    /// <summary>Where the Claude install puts the plugin tree.</summary>
    internal static string ClaudeSkillsDest(string home)
        => Path.Combine(home, ".claude", "skills", PluginFolderName);

    /// <summary>The Claude config file the MCP server is registered in.</summary>
    internal static string ClaudeJson(string home)
        => Path.Combine(home, ".claude.json");

    /// <summary>The Claude install's server exe path. Single source of truth so pre-flight == installer.</summary>
    internal static string ClaudeDestExe(string home)
        => Path.Combine(ClaudeSkillsDest(home), "server", "housecarl-mcp.exe");

    /// <summary>The plugin manifest the Claude install copies, which carries the installed version.</summary>
    internal static string ClaudeDestManifest(string home)
        => Path.Combine(ClaudeSkillsDest(home), ".claude-plugin", "plugin.json");

    /// <summary>Where the Claude install records the skill folders it put under its own skills root, so a later
    /// uninstall takes back exactly those rather than every folder it finds. It sits at the root of the tree the
    /// install owns, beside the copied manifest. An install made before this file existed leaves none, and the
    /// uninstall says which route it took instead — see <see cref="Uninstall"/>.</summary>
    internal static string ClaudeSkillRecord(string home)
        => Path.Combine(ClaudeSkillsDest(home), "installed-skills.txt");

    /// <summary>The shared, cross-agent skills root the Codex install copies skill folders into, flat.</summary>
    internal static string CodexSkillsRoot(string home)
        => Path.Combine(home, ".agents", "skills");

    /// <summary>Codex's own config dir, honouring CODEX_HOME if the user set it.</summary>
    internal static string CodexConfigHome(string home)
    {
        string? codexHomeEnv = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(codexHomeEnv) ? Path.Combine(home, ".codex") : codexHomeEnv;
    }

    /// <summary>The Codex config file the MCP server is registered in.</summary>
    internal static string CodexConfigToml(string home)
        => Path.Combine(CodexConfigHome(home), "config.toml");

    /// <summary>The Codex install's server dir. Under a test home (HOUSECARL_SETUP_HOME) it hangs off that
    /// home so tests never touch the real LOCALAPPDATA; otherwise it lives under %LOCALAPPDATA%.</summary>
    internal static string CodexServerDir(string home, string? homeOverride)
    {
        string dataBase = string.IsNullOrWhiteSpace(homeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : home;
        return Path.Combine(dataBase, "houseCARL", "server");
    }

    /// <summary>The Codex install's server exe path. Single source of truth so pre-flight == installer.</summary>
    internal static string CodexDestExe(string home, string? homeOverride)
        => Path.Combine(CodexServerDir(home, homeOverride), "housecarl-mcp.exe");

    /// <summary>Where the Codex install records the skill folders it put in the shared ~/.agents/skills, so a
    /// later upgrade can take back exactly those and nothing else. It sits in houseCARL's own data dir, beside
    /// the server dir, not in the shared skills dir. The plan names it, because it is a file the install
    /// writes outside the three roots the other lines cover.</summary>
    internal static string CodexSkillRecord(string home, string? homeOverride)
        => Path.Combine(Path.GetDirectoryName(CodexServerDir(home, homeOverride))!, "installed-skills.txt");

    /// <summary>
    /// True if <paramref name="destExe"/> already exists AND can't be opened for writing — i.e. a live
    /// Claude/Codex session is running it (a running image denies write sharing). A missing file (a clean
    /// first install) returns false, so it's never falsely blocked. The handle is opened then immediately
    /// closed and never written, so a held server's exe stays byte-intact.
    ///
    /// CONSERVATIVE BY DESIGN: FileShare.None reports "in use" if ANYTHING else holds the file (an AV
    /// on-demand scan, the Search indexer, a backup tool with full sharing) — a possible false positive
    /// that self-resolves on retry. That is the SAFE direction: a false positive is an annoying "quit and
    /// re-run"; a false negative is the exact mid-copy corruption this exists to prevent. Do NOT "tighten"
    /// this (e.g. to FileShare.Read) into a false-negative.
    /// </summary>
    internal static bool ServerExeInUse(string destExe)
    {
        if (!File.Exists(destExe)) return false;
        try
        {
            using FileStream _ = new(destExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // got exclusive write access -> nothing holds it -> safe to overwrite
        }
        catch (IOException)                 { return true; } // in use by a running session
        catch (UnauthorizedAccessException) { return true; } // locked / read-only -> can't overwrite either
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33): the file-in-use cases. We re-stamp ONLY these
    // as "server in use" so an unrelated IOException (disk full, path too long) still surfaces as the honest
    // generic failure rather than a misleading "quit Claude" message (Q3 — no silently wrong diagnosis).
    private const int HrSharingViolation = unchecked((int)0x80070020);
    private const int HrLockViolation    = unchecked((int)0x80070021);

    internal static bool IsSharingViolation(IOException ex)
        => ex.HResult == HrSharingViolation || ex.HResult == HrLockViolation;

    // ---- server runtime preflight ------------------------------------------

    /// <summary>
    /// Which of the server's required shared frameworks are missing at the required major version.
    /// Asks `dotnet --list-runtimes` first (covers custom install locations on PATH); falls back to
    /// scanning the default machine-wide install dir, which also covers a console whose PATH predates
    /// a just-finished runtime install.
    /// </summary>
    private static List<string> MissingServerRuntimes()
    {
        string[] required = { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" };
        HashSet<string> found = new(StringComparer.Ordinal);

        try
        {
            ProcessStartInfo psi = new("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using Process? p = Process.Start(psi);
            if (p is not null)
            {
                // Drain stderr asynchronously so a broken dotnet host writing errors can't fill the
                // pipe and deadlock the stdout read, and bound the whole interaction so a wedged host
                // can't hang the preflight - on timeout we kill it and fall through to the folder scan.
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
                }
                if (stdoutTask.Wait(2000))
                {
                    foreach (string line in stdoutTask.Result.Split('\n'))
                    {
                        string t = line.Trim();
                        foreach (string fx in required)
                            if (t.StartsWith(fx + " " + ServerRuntimeMajor + ".", StringComparison.Ordinal))
                                found.Add(fx);
                    }
                }
            }
        }
        catch { /* dotnet not on PATH -- the folder scan below still gets a say */ }

        string sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
        foreach (string fx in required)
        {
            if (found.Contains(fx)) continue;
            string fxDir = Path.Combine(sharedDir, fx);
            // A version folder must actually contain assemblies - an empty 9.x dir left behind by an
            // aborted install/uninstall must not count as "installed".
            if (Directory.Exists(fxDir) &&
                Directory.GetDirectories(fxDir, ServerRuntimeMajor + ".*")
                    .Any(d => Directory.EnumerateFiles(d, "*.dll").Any()))
                found.Add(fx);
        }

        return required.Where(fx => !found.Contains(fx)).ToList();
    }

    // ---- target selection (flag or interactive prompt) --------------------

    /// <summary>Whether this run puts houseCARL on the machine or takes it off.</summary>
    public enum Mode { Install, Uninstall }

    /// <summary>
    /// What this run does and to which host(s): the flags if any were passed, else the menu. A run whose input is
    /// redirected has nobody to pick, so it is refused naming the flags rather than defaulting to a host — the
    /// same rule the plan's confirm follows. <c>--uninstall</c> takes the same host flags, so an unattended
    /// removal is <c>--uninstall --both --yes</c>.
    /// </summary>
    private static Answer ResolveChoice(
        string[] args, Detect.HostState claude, Detect.HostState codex, out Mode mode, out Target target)
    {
        mode   = args.Contains("--uninstall") ? Mode.Uninstall : Mode.Install;
        target = Target.Claude;
        if (args.Contains("--both"))   { target = Target.Both;   return Answer.Yes; }
        if (args.Contains("--codex"))  { target = Target.Codex;  return Answer.Yes; }
        if (args.Contains("--claude")) { target = Target.Claude; return Answer.Yes; }

        string verb = mode == Mode.Install ? "install houseCARL for" : "remove houseCARL from";
        if (Console.IsInputRedirected)
        {
            Ui.Problem(
                "Setup cannot ask which agent to " + verb + ", because its input is redirected and "
                + "there is nobody to answer — re-run with --claude, --codex or --both to say which.",
                "Nothing was changed.");
            return Answer.NoOneToAsk;
        }

        if (mode == Mode.Install)
        {
            Ui.Heading("Install houseCARL for which agent?");
            Ui.MenuItem("1", claude.Name, claude.Summary);
            Ui.MenuItem("2", codex.Name,  codex.Summary);
            Ui.MenuItem("3", "Both");
            Ui.MenuItem("4", "Uninstall", Ui.StatusWord.Plain("remove houseCARL instead"));
            Console.WriteLine();
        }
        else
        {
            Ui.Heading("Remove houseCARL from which agent?");
            Ui.MenuItem("1", claude.Name, claude.Summary);
            Ui.MenuItem("2", codex.Name,  codex.Summary);
            Ui.MenuItem("3", "Both");
            Console.WriteLine();
        }

        // Exactly one host on this machine leaves nothing to choose between, so a bare Enter takes it.
        Target? onlyHost = claude.Present && !codex.Present  ? Target.Claude
                         : codex.Present  && !claude.Present ? Target.Codex
                         :                                     null;
        string keys     = mode == Mode.Install ? "1, 2, 3, or 4" : "1, 2, or 3";
        string question = onlyHost is null
            ? "  Enter " + keys + " (or q to quit): "
            : "  Enter " + keys + ", or press Enter for "
              + (onlyHost == Target.Claude ? claude.Name : codex.Name) + " (q to quit): ";

        while (true)
        {
            string? s = Ui.Prompt(question);
            if (s is null)
            {
                // The stream ended mid-question: nobody to ask, same as a redirected run.
                Ui.Problem(
                    "Setup cannot ask which agent to " + verb + ", because its input ended — re-run "
                    + "with --claude, --codex or --both to say which.",
                    "Nothing was changed.");
                return Answer.NoOneToAsk;
            }
            string answer = s.Trim().ToLowerInvariant();
            if (answer.Length == 0 && onlyHost is not null) { target = onlyHost.Value; return Answer.Yes; }
            switch (answer)
            {
                case "1": target = Target.Claude; return Answer.Yes;
                case "2": target = Target.Codex;  return Answer.Yes;
                case "3": target = Target.Both;   return Answer.Yes;
                // The menu's fourth key is the removal, and it asks the same host question over again, because
                // which host to remove from is a different answer from which host to install for.
                case "4" when mode == Mode.Install:
                    return ResolveChoice(args.Append("--uninstall").ToArray(), claude, codex, out mode, out target);
                case "q": case "quit": return Answer.Quit;
                default: Console.WriteLine("  Please type " + keys + ", or q."); break;
            }
        }
    }

    // ---- Claude Code install (unchanged from the proven desktop install) ---

    private static void InstallForClaude(string pluginSrc, string home, List<string> keptBack)
    {
        string skillsDest = ClaudeSkillsDest(home);
        string destExe    = ClaudeDestExe(home);
        string claudeJson = ClaudeJson(home);

        Ui.Step("Claude Code", "installing skills + server", skillsDest);
        CopyDirectory(pluginSrc, skillsDest);

        // CopyDirectory only overwrites, so a skill dropped since the installed version would survive an
        // upgrade and keep loading. This skills root is houseCARL's outright, so anything not in the package
        // is a leftover. A package with no skills folder at all ships no skill list to diff against, so the
        // prune is refused rather than read as "this version dropped every skill".
        string installedSkills = Path.Combine(skillsDest, "skills");
        List<string>? shipped = ShippedSkillNames(pluginSrc);
        if (shipped is null)
        {
            ReportNoSkillsShipped("Claude Code");
        }
        else
        {
            List<string> stale = Directory.Exists(installedSkills)
                ? Directory.GetDirectories(installedSkills)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => !shipped.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();
            ReportRemoved("Claude Code", installedSkills, RemoveSkillDirs(installedSkills, stale, keptBack).Removed);

            // What this install put under its own skills root, so an uninstall removes those by name. The prune
            // above is a directory diff because this root is houseCARL's outright; the record is for the removal,
            // which has no package to diff against.
            File.WriteAllLines(ClaudeSkillRecord(home), shipped);
        }

        Ui.Step("Claude Code", "registering the MCP server", claudeJson);
        RegisterClaudeMcpServer(claudeJson, McpServerName, destExe);
        Console.WriteLine();
    }

    // ---- Codex install -----------------------------------------------------

    private static void InstallForCodex(string pluginSrc, string home, string? homeOverride, List<string> keptBack)
    {
        // Server + corpus go to a neutral per-user dir, NOT the skills dir: Codex scans ~/.agents/skills
        // for skill FOLDERS, and the server is not a skill. Under a test home (HOUSECARL_SETUP_HOME) the
        // data dir hangs off that home so tests never touch the real LOCALAPPDATA.
        string serverDest = CodexServerDir(home, homeOverride);
        string destExe    = CodexDestExe(home, homeOverride);

        // Skills go FLAT under ~/.agents/skills/ (the cross-agent, user-scope skills dir).
        string skillsRoot = CodexSkillsRoot(home);

        // ~/.codex/config.toml, honoring CODEX_HOME if the user set it.
        string configToml = CodexConfigToml(home);

        Ui.Step("Codex", "installing the server", serverDest);
        CopyDirectory(Path.Combine(pluginSrc, "server"), serverDest);

        Ui.Step("Codex", "installing skills", skillsRoot);
        string skillsSrc = Path.Combine(pluginSrc, "skills");
        if (Directory.Exists(skillsSrc))
            foreach (string skillDir in Directory.GetDirectories(skillsSrc))
                CopyDirectory(skillDir, Path.Combine(skillsRoot, Path.GetFileName(skillDir)));

        // Codex-only umbrella skill: the $housecarl entry point (a top-level SKILL.md routing to the
        // helpers + an agents/openai.yaml declaring the MCP-server dependency). It ships beside the plugin
        // in the package (codex/skills/housecarl, the skill dir an immediate child of a skills/ root), NOT
        // inside it, so the Claude install never sees it. Placed in ~/.agents/skills/ alongside the helpers
        // - the location a fresh Codex install was confirmed to scan (the helpers there are discovered and
        // working).
        string umbrellaSrc = Path.Combine(Path.GetDirectoryName(pluginSrc)!, "codex", "skills", "housecarl");
        bool shipsUmbrella = Directory.Exists(umbrellaSrc);
        if (shipsUmbrella)
        {
            string umbrellaDest = Path.Combine(skillsRoot, PluginFolderName);
            Ui.Step("Codex", "installing the houseCARL umbrella skill", umbrellaDest);
            CopyDirectory(umbrellaSrc, umbrellaDest);
        }

        // Drop the folders a previous version put here and this package no longer ships. ~/.agents/skills is
        // shared with every other agent's skills, so this prunes ONLY the folder names houseCARL recorded
        // installing — never a directory diff of a dir we do not own. A package with no skills folder ships no
        // list to reconcile against, so the prune and the record write are both refused.
        List<string>? shippedSkills = ShippedSkillNames(pluginSrc);
        if (shippedSkills is null)
        {
            ReportNoSkillsShipped("Codex");
        }
        else
        {
            // Everything this install put under the shared root, the umbrella included, so a later upgrade
            // can take back exactly those.
            List<string> installedNames = new(shippedSkills);
            if (shipsUmbrella) installedNames.Add(PluginFolderName);

            string recordPath = CodexSkillRecord(home, homeOverride);
            List<string> staleSkills = ReadSkillRecord(recordPath)
                .Where(n => !installedNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var prune = RemoveSkillDirs(skillsRoot, staleSkills, keptBack);
            ReportRemoved("Codex", skillsRoot, prune.Removed);

            // A folder the prune could not take back stays on the record, so the next upgrade tries it again
            // instead of forgetting a leftover houseCARL put there.
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
            File.WriteAllLines(recordPath, installedNames.Concat(prune.Failed));
        }

        Ui.Step("Codex", "registering the MCP server", configToml);
        RegisterCodexMcpServer(configToml, McpServerName, destExe);
        Console.WriteLine();
    }

    /// <summary>The held-file refusal, shared by the install and the removal: the caller's sentence, the path the
    /// seam named, and what "fully" means.</summary>
    private static void ReportServerInUse(string? detailFromSeam, string sentence)
    {
        List<string> detail = new();
        if (detailFromSeam is not null) detail.Add(detailFromSeam);
        detail.Add("");
        detail.Add("\"Fully\" means every desktop window, every terminal session, and any");
        detail.Add("background session.");
        Ui.Problem(sentence, detail.ToArray());
    }

    /// <summary>This exe's own stamped version, with any "+sha" metadata trimmed. build-plugin.ps1 passes
    /// -p:Version from plugin.json at publish, so the banner and the shipped server report the same number;
    /// an unstamped dev build says 0.0.0-dev rather than the 1.0.0 the SDK defaults to.</summary>
    private static string SetupVersion()
    {
        string? info = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "0.0.0-dev";
        int plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    private static int Finish(int exitCode)
    {
        Console.WriteLine();
        Console.Write("Press any key to close...");
        try { Console.ReadKey(intercept: true); } catch { /* no interactive console (redirected) */ }
        Console.WriteLine();
        return exitCode;
    }

    // ---- stale skill folders ----------------------------------------------

    /// <summary>The skill folder names this package ships, or null when the package has no skills folder at
    /// all. Null is not an empty list: a package that ships no skills folder is a broken package (a partial
    /// unzip, a quarantined folder), not a version that dropped every skill, and a prune that read it as the
    /// latter would delete the user's whole installed skill set.</summary>
    private static List<string>? ShippedSkillNames(string pluginSrc)
    {
        string skillsSrc = Path.Combine(pluginSrc, "skills");
        return Directory.Exists(skillsSrc)
            ? Directory.GetDirectories(skillsSrc).Select(d => Path.GetFileName(d)!).ToList()
            : null;
    }

    /// <summary>The skill folder names a previous Codex install recorded. Anything that is not a bare folder
    /// name is dropped, so a hand-edited record can never point the delete below at another path.</summary>
    internal static List<string> ReadSkillRecord(string recordPath)
    {
        if (!File.Exists(recordPath)) return new List<string>();
        return File.ReadAllLines(recordPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "." && l != ".." && l == Path.GetFileName(l))
            .ToList();
    }

    /// <summary>Delete the named skill folders under <paramref name="skillsRoot"/>; returns the ones that went
    /// and the ones that would not. A folder that will not delete (a read-only file inside it, a held handle)
    /// is caught PER FOLDER: the run keeps going to the next one, its path is added to
    /// <paramref name="keptBack"/> for the report at the end, and the install continues to the MCP
    /// registration. Cleanup cannot fail an install that otherwise worked — before the prune existed, the
    /// leftover simply survived and the install succeeded.</summary>
    internal static (List<string> Removed, List<string> Failed) RemoveSkillDirs(
        string skillsRoot, IEnumerable<string> names, List<string> keptBack)
    {
        List<string> removed = new();
        List<string> failed  = new();
        foreach (string name in names)
        {
            string dir = Path.Combine(skillsRoot, name);
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(name); // read-only file inside, or a handle held on one
                keptBack.Add(dir);
            }
        }
        return (removed, failed);
    }

    private static void ReportRemoved(string host, string skillsRoot, List<string> removed)
    {
        if (removed.Count == 0) return;
        Ui.Step(host, "removed skills this version no longer ships", skillsRoot);
        foreach (string name in removed)
            Ui.Bullet(name);
    }

    /// <summary>Said when the package has no skills folder: the prune is refused, and the reason is stated
    /// rather than left as a silently skipped step.</summary>
    private static void ReportNoSkillsShipped(string host)
    {
        Ui.Note(
            "This package has no skills folder, so setup removed no installed " + host + " skill.",
            "A package always ships skills, so unzip the download again if this is not",
            "what you expect.");
    }

    /// <summary>Said at the end of an otherwise finished install: the folders the prune could not delete.</summary>
    private static void ReportKeptBack(List<string> keptBack)
    {
        if (keptBack.Count == 0) return;
        List<string> detail = new(keptBack.Select(dir => "- " + dir)) { "" };
        detail.Add("A file in each is read-only or held open. Nothing else was affected.");
        Ui.Note(
            "houseCARL is installed, but these old skill folders could not be deleted — delete them by "
            + "hand so they stop loading:",
            detail.ToArray());
    }

    // ---- file copy --------------------------------------------------------

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
    }

    // ---- ~/.claude.json registration (JSON splice) ------------------------

    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Insert/replace mcpServers.<paramref name="name"/> WITHOUT reparsing the whole file. ~/.claude.json
    /// can hold keys that differ only by case (Windows path history), which case-insensitive parsers
    /// reject; we parse ONLY the small, duplicate-free mcpServers object and splice it back, leaving the
    /// rest of the file byte-for-byte intact. Backs the file up first.
    /// </summary>
    private static void RegisterClaudeMcpServer(string claudeJsonPath, string name, string command)
    {
        JsonObject entry = new()
        {
            ["type"]    = "stdio",
            ["command"] = command,
            ["args"]    = new JsonArray(),
        };

        if (!File.Exists(claudeJsonPath))
        {
            JsonObject newRoot = new() { ["mcpServers"] = new JsonObject { [name] = entry } };
            File.WriteAllText(claudeJsonPath, newRoot.ToJsonString(Indented));
            return;
        }

        string text = File.ReadAllText(claudeJsonPath);
        File.Copy(claudeJsonPath, claudeJsonPath + ".houseCARL.bak", overwrite: true);

        (int start, int end)? bounds = FindRootMemberObject(text, "mcpServers");
        string updated;
        if (bounds is { } b)
        {
            string objText = text.Substring(b.start, b.end - b.start + 1);
            JsonObject servers = JsonNode.Parse(objText) as JsonObject
                ?? throw new InvalidDataException("mcpServers is not a JSON object.");
            servers[name] = entry; // insert or replace (idempotent on re-run)
            string newObj = Reindent(servers.ToJsonString(Indented), LeadingIndentOfLineAt(text, b.start));
            updated = string.Concat(text.AsSpan(0, b.start), newObj, text.AsSpan(b.end + 1));
        }
        else
        {
            int rootBrace = text.IndexOf('{');
            if (rootBrace < 0) throw new InvalidDataException("~/.claude.json is not a JSON object.");
            JsonObject servers = new() { [name] = entry };
            string block = "\n  \"mcpServers\": " + Reindent(servers.ToJsonString(Indented), "  ") + ",";
            updated = string.Concat(text.AsSpan(0, rootBrace + 1), block, text.AsSpan(rootBrace + 1));
        }

        File.WriteAllText(claudeJsonPath, updated);
    }

    /// <summary>Finds the `{ ... }` value of a DEPTH-1 (root-level) member named <paramref name="key"/>. String-aware.</summary>
    internal static (int start, int end)? FindRootMemberObject(string text, string key)
    {
        string token = "\"" + key + "\"";
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                if (depth == 1 && i + token.Length <= text.Length
                    && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                {
                    int j = i + token.Length;
                    while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                    if (j < text.Length && text[j] == ':')
                    {
                        j++;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && text[j] == '{')
                            return MatchBraces(text, j);
                        throw new InvalidDataException("mcpServers exists but its value is not an object.");
                    }
                }
                inString = true;
            }
            else if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return null;
    }

    private static (int start, int end)? MatchBraces(string text, int openIndex)
    {
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return (openIndex, i); }
        }
        return null;
    }

    internal static string LeadingIndentOfLineAt(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', index) + 1;
        int j = lineStart;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
        return text.Substring(lineStart, j - lineStart);
    }

    internal static string Reindent(string json, string indent)
    {
        if (indent.Length == 0) return json;
        string[] lines = json.Split('\n');
        for (int i = 1; i < lines.Length; i++) lines[i] = indent + lines[i];
        return string.Join('\n', lines);
    }

    // ---- ~/.codex/config.toml registration (TOML splice) ------------------

    /// <summary>The line written above a [mcp_servers.housecarl] table setup created the file for. The removal
    /// splice takes it back with the table, so the two spellings are one const.</summary>
    internal const string TomlComment = "# houseCARL MCP server (added by houseCARL-Setup)";

    /// <summary>
    /// Insert/replace [mcp_servers.<paramref name="name"/>] in a TOML config.toml, leaving everything
    /// else intact. The command path is written as a LITERAL TOML string (single quotes) so Windows
    /// backslashes pass through verbatim - a basic "double-quoted" string would treat them as escapes.
    /// Backs the file up first; idempotent on re-run.
    /// </summary>
    private static void RegisterCodexMcpServer(string configTomlPath, string name, string command)
    {
        string? dir = Path.GetDirectoryName(configTomlPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!File.Exists(configTomlPath))
        {
            string fresh = TomlComment + "\n"
                         + "[mcp_servers." + name + "]\n"
                         + "command = '" + command + "'\n";
            File.WriteAllText(configTomlPath, fresh);
            return;
        }

        string text = File.ReadAllText(configTomlPath);
        File.Copy(configTomlPath, configTomlPath + ".houseCARL.bak", overwrite: true);
        File.WriteAllText(configTomlPath, SpliceTomlTable(text, name, command));
    }

    /// <summary>
    /// Replace the [mcp_servers.&lt;name&gt;] table (and any of its subtables) with a fresh one, or append
    /// it if absent. Line-based so it never reformats the rest of the file; preserves the file's newline
    /// style. A TOML table body runs from its header to the next table header (or EOF).
    /// </summary>
    private static string SpliceTomlTable(string text, string name, string command)
    {
        string nl   = text.Contains("\r\n") ? "\r\n" : "\n";
        string head = "[mcp_servers." + name + "]";
        string sub  = "[mcp_servers." + name + ".";
        string[] body = { head, "command = '" + command + "'" };

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        List<string> outLines = new();
        bool replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!replaced && lines[i].Trim() == head)
            {
                // skip our existing table body + any [mcp_servers.<name>.*] subtables
                int j = i + 1;
                while (j < lines.Length)
                {
                    string t = lines[j].Trim();
                    if (t.StartsWith("[") && t != head && !t.StartsWith(sub)) break;
                    j++;
                }
                outLines.AddRange(body);
                i = j - 1;          // resume after the skipped block
                replaced = true;
                continue;
            }
            outLines.Add(lines[i]);
        }

        if (!replaced)
        {
            string trimmed = string.Join(nl, outLines).TrimEnd('\r', '\n');
            return trimmed.Length == 0
                ? TomlComment + nl + string.Join(nl, body) + nl
                : trimmed + nl + nl + string.Join(nl, body) + nl;
        }

        string result = string.Join(nl, outLines);
        return result.EndsWith(nl) ? result : result + nl;
    }
}

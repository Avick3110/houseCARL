using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL setup tools — the config the USER owns, persisted to houseCARL.user.json (validated loud; nothing saved on a
/// bad value — Q3). Two tools live here:
///   • housecarl_set_mo2_instance — WHERE Mod Organizer 2 is: one path (the instance folder); ModOrganizer.ini yields the
///     mods folder, the ACTIVE profile, and the game Data folder (<see cref="Mo2Instance"/>), so nothing is hand-typed.
///     First-run setup (an unconfigured server's tools return a trained prompt naming this tool) AND switching instances
///     both flow through here.
///   • housecarl_set_tool_path — WHERE an external tool is (the Papyrus compiler, BSArch, or a log folder): the bridge the
///     compile / BSA / log-access riders sit on. Auto-detects canonical homes, so it's usually only needed for BSArch or a
///     non-standard install (<see cref="ToolPathResolver"/> + <see cref="ToolBridge"/>).
/// </summary>
[McpServerToolType]
public static class SetupTools
{
    [McpServerTool(Name = "housecarl_set_mo2_instance", Title = "Tell houseCARL where Mod Organizer 2 is"),
     Description(
         "Point houseCARL at your Mod Organizer 2 instance folder — the folder that contains ModOrganizer.ini (for a " +
         "Wabbajack / portable modlist, that's the list's install folder). houseCARL reads ModOrganizer.ini to derive the " +
         "mods folder, the ACTIVE profile, and the game's Data folder automatically — you give ONLY the one folder, and " +
         "the profile is always auto-detected (you never name it). Use this for FIRST-RUN setup (when a tool reports " +
         "houseCARL isn't configured yet) and to SWITCH to a different MO2 instance later. It VALIDATES the folder is a " +
         "real MO2 instance and reports exactly what's wrong if not (nothing is changed or saved on failure); on success " +
         "it re-points houseCARL immediately (the next read/write resolves against it) and SAVES the choice so it persists " +
         "across restarts. Returns the detected profile plus a quick enabled-mods / active-plugins summary.")]
    public static string SetMo2Instance(
        LoadOrderService svc,
        [Description("Full path to the MO2 instance folder — the one containing ModOrganizer.ini (e.g. a Wabbajack list's install folder).")]
            string path) => Guard.Tool("housecarl_set_mo2_instance", () =>
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: no path given. Pass the full path to your MO2 instance folder (the one containing ModOrganizer.ini).";

        Mo2InstancePaths paths; bool persisted; string? persistError; string? persistNote;
        try { (paths, persisted, persistError, persistNote) = svc.SetInstance(path); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }   // not a usable instance — Q3 reason, nothing changed

        return Render(paths, persisted, persistError, persistNote);
    });

    [McpServerTool(Name = "housecarl_set_tool_path", Title = "Tell houseCARL where an external tool is"),
     Description(
         "Give houseCARL the path to an external tool it drives: 'papyrus_compiler' (the Creation Kit's " +
         "PapyrusCompiler.exe, for compiling .psc scripts to .pex), 'bsarch' (BSArch.exe, for .bsa archive " +
         "list/extract/repack), 'papyrus_logs' (the Papyrus script-log FOLDER), or 'crash_logs' (the SKSE crash-log " +
         "FOLDER) — the bridge houseCARL's compile / BSA / log-reading capabilities sit on. houseCARL AUTO-DETECTS the " +
         "canonical homes for the compiler and the log folders, so you usually only need this for BSArch (no fixed home) " +
         "or a non-standard install. VALIDATES the path — the .exe exists and looks like the right tool; the log folder " +
         "exists — and reports exactly what's wrong if not, saving NOTHING on failure (Q3). On success it SAVES the choice " +
         "to houseCARL.user.json so it persists across restarts, coexisting with your MO2 instance setting. tool must be " +
         "one of: papyrus_compiler, bsarch, papyrus_logs, crash_logs.")]
    public static string SetToolPath(
        ToolPathResolver bridge,
        [Description("Which tool: 'papyrus_compiler' (CK PapyrusCompiler.exe), 'bsarch' (BSArch.exe), 'papyrus_logs' (script-log folder), or 'crash_logs' (SKSE crash-log folder).")]
            string tool,
        [Description("Full path to the tool: the .exe FILE for papyrus_compiler/bsarch, or the log DIRECTORY for papyrus_logs/crash_logs.")]
            string path) => Guard.Tool("housecarl_set_tool_path", () =>
    {
        if (string.IsNullOrWhiteSpace(tool))
            return "error: no tool named. Pass tool= one of: " + ToolBridge.WireKeys + ".";
        if (!ToolBridge.TryParse(tool, out var dep))
            return $"error: unknown tool '{tool}'. Expected one of: {ToolBridge.WireKeys}.";
        if (string.IsNullOrWhiteSpace(path))
            return $"error: no path given for '{tool}'. Pass the full path to {ToolBridge.Info(dep).Display}.";

        var (ok, error, persisted, persistError, persistNote, resolved) = bridge.Save(dep, path);
        if (!ok) return "error: " + error;   // validation failed — nothing saved (Q3)

        var info = ToolBridge.Info(dep);
        var sb = new StringBuilder();
        sb.Append("configured houseCARL -> ").Append(info.Display).Append('\n');
        sb.Append("  path: ").Append(resolved).Append('\n');
        sb.Append(persisted
            ? "saved to houseCARL.user.json — persists across restarts (coexists with your MO2 instance)."
            : $"NOTE: could not save ({persistError}) — works this session, but you'll need to set it again after a restart.");
        if (persistNote is not null) sb.Append("\nRECOVERED: ").Append(persistNote);   // corrupt prior config — never silent (hunt F3)
        return sb.ToString();
    });

    /// <summary>Confirmation: the instance + the DERIVED roots + the AUTO-DETECTED profile, a cheap enabled/active summary
    /// (text-file read, no deep index — proof houseCARL found the order), and whether the choice was persisted (Q3: a
    /// failed save is reported, not hidden; a corrupt-file recovery is named even on success — hunt F3).</summary>
    static string Render(Mo2InstancePaths p, bool persisted, string? persistError, string? persistNote)
    {
        var sb = new StringBuilder();
        sb.Append("configured houseCARL -> MO2 instance '").Append(p.InstanceDir).Append("'\n");
        sb.Append("active profile: ").Append(p.ProfileName).Append("  (auto-detected from ModOrganizer.ini)\n");
        sb.Append("  mods folder: ").Append(p.ModsDir).Append('\n');
        sb.Append("  game Data  : ").Append(p.DataDir).Append('\n');

        // Cheap composition (the three profile text files only — NO deep index): a quick figure so the user sees houseCARL
        // actually found the order. The resolve already confirmed the profile files exist; a read hiccup here is non-fatal.
        try
        {
            var comp = Mo2LoadOrder.ReadComposition(p.ProfileDir);
            int active = comp.ActivePluginNames.Count + comp.ImplicitPluginNames.Count;
            sb.Append("sees: ").Append(comp.EnabledMods.Count).Append(" enabled mods · ")
              .Append(comp.OrderedPluginNames.Count).Append(" plugins in the load order (").Append(active).Append(" active)\n");
        }
        catch { /* non-fatal — don't fail a good setup over a follow-up read */ }

        sb.Append(persisted
            ? "saved to houseCARL.user.json — persists across restarts."
            : $"NOTE: could not save the choice ({persistError}) — it works this session, but you'll need to set it again after a restart.");
        if (persistNote is not null) sb.Append("\nRECOVERED: ").Append(persistNote);   // corrupt prior config — never silent (hunt F3)
        sb.Append("\nthe load order resolves on the next read/write (first build ~10s).");
        return sb.ToString();
    }
}

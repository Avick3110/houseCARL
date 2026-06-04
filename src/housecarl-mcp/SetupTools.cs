using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL setup tool — the one piece of config the USER owns: WHERE Mod Organizer 2 is. houseCARL needs a single path
/// (the MO2 instance folder); it reads ModOrganizer.ini to derive the mods folder, the ACTIVE profile, and the game Data
/// folder (<see cref="Mo2Instance"/>), so the user never hand-types those and the profile is always auto-detected.
/// First-run setup (an unconfigured server's tools return a trained prompt naming this tool) AND switching between MO2
/// instances both flow through here. Validates loud (Q3 — nothing changes on a bad path) and persists the choice to
/// houseCARL.user.json so it survives a restart. The 9th MCP tool.
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
            string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: no path given. Pass the full path to your MO2 instance folder (the one containing ModOrganizer.ini).";

        Mo2InstancePaths paths; bool persisted; string? persistError;
        try { (paths, persisted, persistError) = svc.SetInstance(path); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }   // not a usable instance — Q3 reason, nothing changed

        return Render(paths, persisted, persistError);
    }

    /// <summary>Confirmation: the instance + the DERIVED roots + the AUTO-DETECTED profile, a cheap enabled/active summary
    /// (text-file read, no deep index — proof houseCARL found the order), and whether the choice was persisted (Q3: a
    /// failed save is reported, not hidden).</summary>
    static string Render(Mo2InstancePaths p, bool persisted, string? persistError)
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
        sb.Append("\nthe load order resolves on the next read/write (first build ~10s).");
        return sb.ToString();
    }
}

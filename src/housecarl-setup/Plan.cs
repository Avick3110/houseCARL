namespace HousecarlSetup;

/// <summary>
/// What the run is about to do, printed before it does any of it. Every destination here comes from the same
/// path helpers on <see cref="Program"/> that <see cref="Program.TryInstall"/> copies to, so the plan cannot say
/// one path and the install write another.
/// </summary>
public static class Plan
{
    /// <summary>One destination the install touches, and what it puts there.</summary>
    public sealed record Line(string Path, string What);

    /// <summary>One host's share of the plan.</summary>
    public sealed record HostPlan(Detect.HostState Host, IReadOnlyList<Line> Lines);

    /// <summary>The plan for a target, host by host, in install order.</summary>
    public static List<HostPlan> For(
        Program.Target target, string home, string? homeOverride,
        Detect.HostState claude, Detect.HostState codex)
    {
        List<HostPlan> plans = new();

        if (target is Program.Target.Claude or Program.Target.Both)
            plans.Add(new HostPlan(claude, new List<Line>
            {
                new(Program.ClaudeSkillsDest(home), "skills + server"),
                new(Program.ClaudeJson(home),       "registers the server  (backed up first)"),
            }));

        if (target is Program.Target.Codex or Program.Target.Both)
            plans.Add(new HostPlan(codex, new List<Line>
            {
                new(Program.CodexServerDir(home, homeOverride), "server"),
                new(Program.CodexSkillsRoot(home),              "skills"),
                new(Program.CodexConfigToml(home),              "registers the server  (backed up first)"),
            }));

        return plans;
    }

    /// <summary>Print the plan: a heading per host saying install or upgrade, then one line per destination.</summary>
    public static void Print(List<HostPlan> plans, string version)
    {
        bool everyHostUpgrading = plans.Count > 0 && plans.All(p => p.Host.Installed);
        Ui.Heading(everyHostUpgrading ? "This will replace:" : "This will write:");

        // One path column across every host, so the "what" side reads as a column rather than a ragged edge.
        int pathWidth = plans.SelectMany(p => p.Lines).Select(l => l.Path.Length).DefaultIfEmpty(0).Max();

        bool first = true;
        foreach (HostPlan plan in plans)
        {
            if (!first) Console.WriteLine();
            first = false;
            Ui.PlanHost(plan.Host.Name, HostAction(plan.Host, version));
            foreach (Line line in plan.Lines)
                Ui.PlanLine(line.Path, line.What, pathWidth);
        }

        Ui.Body("Nothing else is touched. Your game, your mods and your MO2 profile are not read.");
    }

    /// <summary>What this run is to this host: a first install, or an upgrade off the version already there.</summary>
    private static string HostAction(Detect.HostState host, string version)
        => !host.Installed                ? "install " + version
         : host.InstalledVersion is null  ? "upgrade to " + version
         : host.InstalledVersion == version ? "reinstall " + version
         :                                   "upgrade " + host.InstalledVersion + " to " + version;
}

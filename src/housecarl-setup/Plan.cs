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
                new(Program.ClaudeJson(home),       BacksUpFirst),
            }));

        if (target is Program.Target.Codex or Program.Target.Both)
            plans.Add(new HostPlan(codex, new List<Line>
            {
                new(Program.CodexServerDir(home, homeOverride),    "server"),
                new(Program.CodexSkillsRoot(home),                 "skills"),
                new(Program.CodexSkillRecord(home, homeOverride),  "records which skills were installed"),
                new(Program.CodexConfigToml(home),                 BacksUpFirst),
            }));

        return plans;
    }

    /// <summary>What a host-config line puts there. The install copies the file to a ".houseCARL.bak"
    /// beside it before editing it, and that copy is a file the plan would otherwise not name.</summary>
    private const string BacksUpFirst = "registers the server  (a .houseCARL.bak copy is made first)";

    /// <summary>Print the plan: a heading per host saying install or upgrade, then one line per destination.</summary>
    public static void Print(List<HostPlan> plans, string version)
    {
        bool anyHostInstalled = plans.Any(p => p.Host.Installed);
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

        // The run deletes as well as writes, and the paths above are the only place that shows. A host with
        // nothing installed has no previous skill set to take folders back from, so the sentence is printed
        // only when one of the hosts above is being replaced.
        List<string> body = new();
        if (anyHostInstalled)
        {
            body.Add("Under the skills paths above, skill folders a previous houseCARL");
            body.Add("installed and this version no longer ships are deleted.");
        }
        body.Add("Your game, your mods and your MO2 profile are not read.");
        Ui.Body(body.ToArray());
    }

    /// <summary>
    /// What this run is to this host: a first install, or what it does to the version already there. The two
    /// versions are compared, not just tested for equality, so a package older than the install says
    /// "downgrade" rather than claiming the opposite of what it is about to do. A version either side cannot
    /// parse gets "replace", which is true whichever direction it turns out to be.
    /// </summary>
    internal static string HostAction(Detect.HostState host, string version)
    {
        if (!host.Installed) return "install " + version;

        string? installed = host.InstalledVersion;
        if (installed is null) return "replace the installed version with " + version;
        if (installed == version) return "reinstall " + version;

        if (Number(installed) is not { } had || Number(version) is not { } coming)
            return "replace " + installed + " with " + version;

        int cmp = had.CompareTo(coming);
        // Equal numbers with unequal strings is a pre-release either side (2.0.0-rc1 against 2.0.0), where the
        // direction is real but not in the numbers, so this says what it knows rather than "reinstall".
        return cmp < 0 ? "upgrade "   + installed + " to " + version
             : cmp > 0 ? "downgrade " + installed + " to " + version
             :           "replace "   + installed + " with " + version;
    }

    /// <summary>
    /// The numeric part of a version, or null when it does not parse. A pre-release or build suffix
    /// ("2.0.0-rc1", "2.0.0+abc") is cut off first, and the unspecified components are filled with zero so a
    /// manifest's "2.0.0" and an exe resource's "2.0.0.0" compare equal instead of one reading as newer.
    /// </summary>
    private static Version? Number(string text)
    {
        int cut = text.IndexOfAny(new[] { '-', '+' });
        string numeric = cut >= 0 ? text[..cut] : text;
        if (!Version.TryParse(numeric, out Version? v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}

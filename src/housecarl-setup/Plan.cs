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

    /// <summary>The plan for a removal, host by host, in removal order. Same destinations as the install, said
    /// as what comes off them, so the two cannot name different paths.</summary>
    public static List<HostPlan> ForRemoval(
        Program.Target target, string home, string? homeOverride,
        Detect.HostState claude, Detect.HostState codex)
    {
        List<HostPlan> plans = new();

        if (target is Program.Target.Claude or Program.Target.Both)
            plans.Add(new HostPlan(claude, new List<Line>
            {
                new(Program.ClaudeSkillsDest(home), "skills, server, and the saved MO2 instance"),
                new(Program.ClaudeJson(home),       RemovesEntry),
            }));

        if (target is Program.Target.Codex or Program.Target.Both)
            plans.Add(new HostPlan(codex, new List<Line>
            {
                new(Program.CodexServerDir(home, homeOverride),   "server, and the saved MO2 instance"),
                new(Program.CodexSkillsRoot(home),                "only the skills the record below names"),
                new(Program.CodexSkillRecord(home, homeOverride), "the record itself"),
                new(Program.CodexConfigToml(home),                RemovesEntry),
            }));

        return plans;
    }

    /// <summary>What a host-config line puts there. The install copies the file to a ".houseCARL.bak"
    /// beside it before editing it, and that copy is a file the plan would otherwise not name.</summary>
    private const string BacksUpFirst = "registers the server  (a .houseCARL.bak copy is made first)";

    /// <summary>The removal's version of the line above: the same file, the same backup, the entry taken out.</summary>
    private const string RemovesEntry = "removes the server entry  (a " + Uninstall.BackupSuffix + " copy is made first)";

    /// <summary>Print the plan: a heading per host saying install, upgrade or removal, then one line per
    /// destination.</summary>
    public static void Print(List<HostPlan> plans, string version, bool removing = false)
    {
        bool anyHostInstalled = plans.Any(p => p.Host.Installed);
        bool everyHostUpgrading = plans.Count > 0 && plans.All(p => p.Host.Installed);
        Ui.Heading(removing ? "This will remove:" : everyHostUpgrading ? "This will replace:" : "This will write:");

        // One path column across every host, so the "what" side reads as a column rather than a ragged edge.
        int pathWidth = plans.SelectMany(p => p.Lines).Select(l => l.Path.Length).DefaultIfEmpty(0).Max();

        bool first = true;
        foreach (HostPlan plan in plans)
        {
            if (!first) Console.WriteLine();
            first = false;
            Ui.PlanHost(plan.Host.Name, removing ? RemovalAction(plan.Host) : HostAction(plan.Host, version));
            foreach (Line line in plan.Lines)
                Ui.PlanLine(line.Path, line.What, pathWidth);
        }

        // The run deletes as well as writes, and the paths above are the only place that shows. A host with
        // nothing installed has no previous skill set to take folders back from, so the sentence is printed
        // only when one of the hosts above is being replaced.
        List<string> body = new();
        if (removing)
        {
            body.Add("Nothing else in those two config files is touched, and the patches");
            body.Add("houseCARL wrote — which live in your MO2 mods folder — are left alone.");
        }
        else
        {
            if (anyHostInstalled)
            {
                body.Add("Under the skills paths above, skill folders a previous houseCARL");
                body.Add("installed and this version no longer ships are deleted.");
            }
            body.Add("Your game, your mods and your MO2 profile are not read.");
        }
        Ui.Body(body.ToArray());
    }

    /// <summary>
    /// The block that closes a finished install: what went where, taken from the same plan the confirm was
    /// given for, and then what the person does next. An install writes every line of its plan, so the plan is
    /// what it did.
    /// </summary>
    public static void PrintSummary(List<HostPlan> plans, string version)
    {
        List<string> hosts = plans.Select(p => p.Host.Name).ToList();

        Console.WriteLine();
        Ui.Rule();
        Ui.Done("houseCARL " + version + " is installed for " + string.Join(" and ", hosts) + ".");
        PrintBlock("Written", plans.Select(p => (p.Host.Name, (IReadOnlyList<Line>)p.Lines)).ToList());

        Ui.Heading("Next");
        foreach (string host in hosts) Ui.Bullet(host + ": fully quit and reopen it.");
        FullyMeans();
        Ui.Bullet("On the first use of a houseCARL tool it asks you for your Mod Organizer 2");
        Console.WriteLine("        folder, the one holding ModOrganizer.ini.");
        if (plans.Count > 1)
            Ui.Bullet("Each host runs its own server copy, so that folder is set once per host.");
        Console.WriteLine();
    }

    /// <summary>
    /// The block that closes a finished removal. Unlike the install's, it is NOT the plan printed again: a
    /// removal's plan is what the run intends to take off, and a host with nothing installed, or a config with
    /// no entry of ours, is a line the run does not act on. Every line here is what one step actually did,
    /// carried back from <see cref="Uninstall.TryUninstall"/>, so the block cannot claim a removal that did not
    /// happen.
    /// </summary>
    public static void PrintRemovalSummary(IReadOnlyList<Uninstall.HostResult> hosts)
    {
        string names = string.Join(" and ", hosts.Select(h => h.Host));
        bool anything = hosts.Any(h => h.RemovedSomething);

        Console.WriteLine();
        Ui.Rule();
        Ui.Done(anything
            ? "houseCARL is removed from " + names + "."
            : "There was no houseCARL to remove from " + names + ", so nothing was changed.");

        PrintBlock(anything ? "Removed" : "Looked at", hosts
            .Select(h => (h.Host, (IReadOnlyList<Line>)h.Steps.Select(s => new Line(s.Path, s.Did)).ToList()))
            .ToList());

        if (anything)
        {
            Ui.Heading("Next");
            foreach (var h in hosts.Where(h => h.RemovedSomething)) Ui.Bullet(h.Host + ": fully quit and reopen it.");
            FullyMeans();
        }
        Console.WriteLine();
    }

    /// <summary>The path-and-what block both summaries print, one heading and one group per host.</summary>
    private static void PrintBlock(string heading, List<(string Host, IReadOnlyList<Line> Lines)> blocks)
    {
        Ui.Heading(heading);
        int pathWidth = blocks.SelectMany(b => b.Lines).Select(l => l.Path.Length).DefaultIfEmpty(0).Max();
        bool first = true;
        foreach (var block in blocks)
        {
            if (!first) Console.WriteLine();
            first = false;
            Ui.PlanHost(block.Host, "");
            foreach (Line line in block.Lines)
                Ui.PlanLine(line.Path, line.What, pathWidth);
        }
    }

    private static void FullyMeans()
    {
        Console.WriteLine("        \"Fully\" means every window, every terminal session, and any");
        Console.WriteLine("        background session.");
    }

    /// <summary>What this run is to this host on a removal: what is there to take, said before it is taken.</summary>
    internal static string RemovalAction(Detect.HostState host)
        => !host.Installed          ? "houseCARL is not installed for it"
         : host.InstalledVersion is null ? "remove the installed houseCARL"
         :                            "remove houseCARL " + host.InstalledVersion;

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

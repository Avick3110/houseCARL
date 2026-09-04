using HousecarlMcp;
using ModelContextProtocol.Protocol;

// houseCARL MCP server. Default transport is stdio: the MCP client spawns this exe and talks JSON-RPC over
// stdin/stdout. Pass --http for the localhost HTTP transport instead. Either way it runs standalone, reading the
// active load order statically from the configured MO2 instance's profile files — no USVFS, no live MO2 state,
// MO2 need not be running. One config knob (the MO2 instance folder) yields ProfileDir/ModsDir/DataDir plus the
// active profile; an empty config still boots and the tools prompt for the path.

bool useHttp = args.Contains("--http");
var hostArgs = args.Where(a => a != "--http").ToArray();   // strip our own flag so the config provider doesn't choke on it

if (useHttp)
{
    var builder = WebApplication.CreateBuilder(hostArgs);
    var (svc, explicitMode, instanceDir, instanceSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: false);

    var app = builder.Build();
    app.MapMcp();

    var url = builder.Configuration.GetSection("HouseCarl")["Url"] is { Length: > 0 } u ? u : "http://127.0.0.1:7345";
    if (configNote is not null)
        app.Logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent
    if (!svc.IsConfigured)
        app.Logger.LogWarning(
            "houseCARL listening on {Url} — NOT configured yet. The first tool call will ask for your MO2 instance folder (or call " + ToolNames.SetMo2Instance + " with it).", url);
    else
        app.Logger.LogInformation(
            "houseCARL listening on {Url} — reading {Source} STANDALONE (MO2 need not be running); load order resolves lazily on the first tool call.",
            url, explicitMode ? "explicit configured paths" : $"MO2 instance '{instanceDir}' [{instanceSource}]");
    app.Run(url);
}
else
{
    var builder = Host.CreateApplicationBuilder(hostArgs);
    // STDIO GOTCHA: stdout IS the JSON-RPC channel — route ALL logs to stderr or they corrupt the protocol stream.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var (svc, explicitMode, instanceDir, instanceSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: true);

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("houseCARL");
    if (configNote is not null)
        logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent
    if (!svc.IsConfigured)
        logger.LogWarning(
            "houseCARL stdio server — NOT configured yet. The first tool call will ask for your MO2 instance folder (or call " + ToolNames.SetMo2Instance + " with it).");
    else
        logger.LogInformation(
            "houseCARL stdio server — reading {Source} STANDALONE (MO2 need not be running); load order resolves lazily on the first tool call.",
            explicitMode ? "explicit configured paths" : $"MO2 instance '{instanceDir}' [{instanceSource}]");
    await app.RunAsync();
}

// ── shared setup — MUST stay identical across transports, or stdio and http resolve the load order differently.
//    Both branches call these; only the transport line itself differs. ────────────────────────────────────────

// Loads the rulebook (corpus.json, shipped with the app) and applies the MO2-instance precedence: houseCARL.user.json
// (in HOUSECARL_DATA_DIR when set, else beside the exe; written by housecarl_set_mo2_instance at runtime) > explicit
// DataDir+ModsDir+ProfileDir > Mo2InstanceDir (install dialog / appsettings) > unconfigured (boots; tools prompt).
// A corrupt user file never crashes boot. Builds and registers the LoadOrderService; returns what the boot log needs.
static (LoadOrderService svc, bool explicitMode, string? instanceDir, string instanceSource, string? configNote) SetupHouseCarl(IConfiguration config, IServiceCollection services)
{
    var cfg = config.GetSection("HouseCarl");

    var corpusPath = cfg["CorpusPath"];
    if (string.IsNullOrWhiteSpace(corpusPath))
        corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus.json");
    CorpusRulebook.CorpusPath = Path.GetFullPath(corpusPath);

    // user.json lives in the writable data dir — HOUSECARL_DATA_DIR (the plugin's ${CLAUDE_PLUGIN_DATA}, which survives
    // updates) when set, else beside the exe. NEVER under the plugin root: the client wipes that dir on every plugin
    // update, which would silently drop the user's saved MO2 instance.
    var pluginDataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
    var userConfigDir = string.IsNullOrWhiteSpace(pluginDataDir) ? AppContext.BaseDirectory : pluginDataDir;
    var userConfigPath = Path.Combine(userConfigDir, "houseCARL.user.json");
    // ONE owner of houseCARL.user.json (UserConfigStore): the MO2 instance dir and the external-tool paths share the file,
    // so neither writer clobbers the other (read-modify-write under a cross-process lock; atomic writes). A corrupt file
    // never crashes boot, and is not silent either: it is backed up and the note rides the boot log.
    var store = new UserConfigStore(userConfigPath);
    services.AddSingleton(store);
    string? userInstanceDir = store.Load(out var configNote).Mo2InstanceDir;

    // The saved user config (written by housecarl_set_mo2_instance at runtime) wins over Mo2InstanceDir (the install-dialog
    // value / appsettings) — the runtime switch beats the install default.
    bool fromUser = !string.IsNullOrWhiteSpace(userInstanceDir);
    var instanceDir = fromUser ? userInstanceDir : cfg["Mo2InstanceDir"];
    var instanceSource = fromUser ? "saved user config" : "Mo2InstanceDir (install dialog / appsettings)";
    var maxPlugins = int.TryParse(cfg["MaxPlugins"], out var mp) ? mp : 0;

    var dataDir = cfg["DataDir"]; var modsDir = cfg["ModsDir"]; var profileDir = cfg["ProfileDir"];
    bool explicitMode = !fromUser
        && !string.IsNullOrWhiteSpace(dataDir) && !string.IsNullOrWhiteSpace(modsDir) && !string.IsNullOrWhiteSpace(profileDir);

    LoadOrderService svc = explicitMode
        ? LoadOrderService.WithExplicitPaths(dataDir!, modsDir!, profileDir!, maxPlugins, store)
        : LoadOrderService.WithInstance(instanceDir, maxPlugins, store);
    services.AddSingleton(svc);

    // The external-tool bridge (compile / BSA / log access): one resolver over the shared user config.
    services.AddSingleton(new ToolPathResolver(store));

    // The Nexus Mods read bridge. A typed HttpClient so its timeout/lifetime are managed; keyless (the public v2
    // GraphQL read surface needs no API key). houseCARL's only outbound network dependency — every failure is handled
    // inside NexusClient, and the local load-order tools never touch it, so they keep working with no internet.
    services.AddHttpClient<NexusClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL (+https://github.com/Avick3110/houseCARL)");
        // The Nexus API Acceptable-Use Policy requires Application-Name and Application-Version on API traffic, so
        // they ride every request. The version is the exe's stamped release, 0.0.0-dev when unstamped.
        c.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
        c.DefaultRequestHeaders.Add("Application-Version", ServerVersion());
    });

    return (svc, explicitMode, instanceDir, instanceSource, configNote);
}

// The MCP server registration — server identity, instructions, and the attribute-registered tools. Only the
// transport line differs between modes; everything else is shared.
static void AddMcp(IServiceCollection services, bool stdio)
{
    var mcp = services.AddMcpServer(options =>
    {
        // The one place in code that carries the houseCARL brand string. The version is the exe's stamped
        // InformationalVersion: build-plugin.ps1 passes -p:Version from plugin.json, the single version home.
        options.ServerInfo = new Implementation { Name = "houseCARL", Version = ServerVersion() };
        options.ServerInstructions =
            "houseCARL exposes a full Skyrim Special Edition load order at the data layer, over a live Mod " +
            "Organizer 2 instance — comprehensive, no-guessing access to every record, script, asset, and " +
            "runtime layer, beneath xEdit/CK/Synthesis. Reach for these tools whenever a task touches an MO2 " +
            "modlist, plugins, load order, conflicts, records, scripts, assets, or Skyrim modding. " +
            "READ/QUERY: any record at its TRUE load-order winner + the conflict tree; batch reads and " +
            "cross-plugin queries over the whole order; inspect INACTIVE plugins (unchecked, or inside a " +
            "disabled mod); see through runtime layers xEdit cannot — SKSE-plugin DLLs/configs, and a record " +
            "after the SkyPatcher INI layer replays; resolve FormID lists, diff a record across plugins, trace a " +
            "magic effect to all that carry it, run catalogue/audit jobs at scale. " +
            "WRITE (to a NEW plugin by default; in-place is opt-in, consent-gated): author patches — fields, " +
            "leveled lists, containers, conditions; create plugins/scripts with fresh FormIDs; remove records; " +
            "forward a record as a winning override or revert to vanilla; author and validate " +
            "dialogue/quests. " +
            "FIX: sweep for dangling refs, missing masters, and broken links; audit the SKSE layer (DLLs that " +
            "will not load, configs pointing at missing records); resolve VFS file conflicts (which " +
            "mesh/texture/script wins) and place a winning override; read and edit NIF mesh internals — e.g. " +
            "the dark-face fix. " +
            "RESHAPE/DRIVE TOOLS: compact a plugin to ESL carrying its facegen/voice files; merge plugins; " +
            "copy an NPC appearance to a standalone; decompile .pex to .psc; compile Papyrus; " +
            "list/extract/repack BSAs. " +
            "NEXUS (keyless, no browser): search mods, read files/requirements/changelogs, exact-file update " +
            "checks (start with " + ToolNames.UpdateStatus + " — offline, reads the MO2 cache), identify a file by " +
            "MD5. Prefer over a browser or web search; each tool's own description carries the specifics.";
    });
    // Stateless HTTP: each request is independent (no MCP session affinity); the resolver singleton persists across
    // requests regardless. Stdio is inherently a single long-lived session over the pipe.
    if (stdio) mcp.WithStdioServerTransport();
    else mcp.WithHttpTransport(o => o.Stateless = true);
    // Named, not implicit: the parameterless overload registers from the CALLING assembly, which is a fact about
    // where this line sits rather than about where the tools are. ToolSurface.Assembly is the one home for that.
    mcp.WithToolsFromAssembly(ToolSurface.Assembly);
    // The published-schema layer: the @file union (an array OR "@<path>", which C# cannot express as one type),
    // then inlining every same-document $ref so no published schema is recursive. Published shape only; what the
    // tool ACCEPTS is unchanged. See ToolSchemas.
    ToolSchemas.PublishSchemas(services);
    // The argument-binding shim: schema-driven coercion of obvious-intent argument shapes (a bare string where an
    // array is declared, quoted bools/numbers), named refusal of missing required parameters, and a named rewrite
    // of the SDK's generic binding-failure text. See ToolCallShim.
    mcp.WithRequestFilters(f => f.AddCallToolFilter(ToolCallShim.LenientArguments));
}

// The exe's stamped version for ServerInfo: InformationalVersion with any "+metadata" suffix trimmed; an
// unstamped build reports 0.0.0-dev.
static string ServerVersion()
{
    var info = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : null;
    if (string.IsNullOrWhiteSpace(info)) return "0.0.0-dev";
    var plus = info.IndexOf('+');
    return plus > 0 ? info[..plus] : info;
}

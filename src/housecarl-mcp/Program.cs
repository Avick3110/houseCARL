using HousecarlMcp;
using ModelContextProtocol.Protocol;

// houseCARL MCP server. DEFAULT transport is STDIO (the 1.0 launch model): the MCP client (Claude Code) spawns
// this exe and talks JSON-RPC over stdin/stdout — no port, no console window, no manual start. Pass --http to run
// the localhost HTTP transport instead (kept for the curl-driven dev proofs). EITHER way it runs STANDALONE: it
// reads the TRUE active load order STATICALLY from the configured MO2 instance's profile files (§8.5 — no USVFS, no
// live MO2 state; MO2 need not be running). ONE config knob — the MO2 instance folder — yields ProfileDir/ModsDir/
// DataDir + the active profile (Mo2Instance, from ModOrganizer.ini); an empty config BOOTS anyway and the tools
// prompt the user for the path. Tools (attribute-registered) ride the PROVEN housecarl-core.

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
        app.Logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent (hunt F3)
    if (!svc.IsConfigured)
        app.Logger.LogWarning(
            "houseCARL listening on {Url} — NOT configured yet. The first tool call will ask for your MO2 instance folder (or call housecarl_set_mo2_instance with it).", url);
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
        logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent (hunt F3)
    if (!svc.IsConfigured)
        logger.LogWarning(
            "houseCARL stdio server — NOT configured yet. The first tool call will ask for your MO2 instance folder (or call housecarl_set_mo2_instance with it).");
    else
        logger.LogInformation(
            "houseCARL stdio server — reading {Source} STANDALONE (MO2 need not be running); load order resolves lazily on the first tool call.",
            explicitMode ? "explicit configured paths" : $"MO2 instance '{instanceDir}' [{instanceSource}]");
    await app.RunAsync();
}

// ── shared setup — MUST stay identical across transports (divergence here = stdio and http resolving the load
//    order differently, a latent bug). Both branches call these; only the transport line itself differs. ──────────

// houseCARL's OWN rulebook (corpus.json, shipped WITH the app) + the MO2-instance precedence (§6d): houseCARL.user.json
// (in HOUSECARL_DATA_DIR = ${CLAUDE_PLUGIN_DATA} when set, else beside the exe; written by housecarl_set_mo2_instance at
// RUNTIME) > explicit DataDir+ModsDir+ProfileDir (dev/non-portable) > Mo2InstanceDir (userConfig install dialog / appsettings)
// > UNCONFIGURED (boots; tools prompt). The runtime user choice beats the install default. A corrupt user file never crashes boot (Q3).
// Builds + registers the LoadOrderService; returns the bits the boot log needs.
static (LoadOrderService svc, bool explicitMode, string? instanceDir, string instanceSource, string? configNote) SetupHouseCarl(IConfiguration config, IServiceCollection services)
{
    var cfg = config.GetSection("HouseCarl");

    var corpusPath = cfg["CorpusPath"];
    if (string.IsNullOrWhiteSpace(corpusPath))
        corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus.json");
    CorpusRulebook.CorpusPath = Path.GetFullPath(corpusPath);

    // user.json lives in the WRITABLE data dir — HOUSECARL_DATA_DIR (the plugin's ${CLAUDE_PLUGIN_DATA}, which survives
    // updates) when set, else beside the exe (dev / non-plugin). NEVER under the plugin root: the client wipes that dir on
    // every plugin update, which would silently drop the user's saved MO2 instance (rulebook §6c / F1).
    var pluginDataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
    var userConfigDir = string.IsNullOrWhiteSpace(pluginDataDir) ? AppContext.BaseDirectory : pluginDataDir;
    var userConfigPath = Path.Combine(userConfigDir, "houseCARL.user.json");
    // ONE owner of houseCARL.user.json (UserConfigStore): the MO2 instance dir AND the external-tool paths share the file,
    // so neither writer clobbers the other (read-modify-write under a cross-process lock; atomic writes). A corrupt file
    // never crashes boot, but it is NOT silent either (hunt F3): it's backed up and the note rides the boot log.
    var store = new UserConfigStore(userConfigPath);
    services.AddSingleton(store);
    string? userInstanceDir = store.Load(out var configNote).Mo2InstanceDir;

    // PRECEDENCE (§6d): the saved user config (houseCARL.user.json, written by housecarl_set_mo2_instance at RUNTIME) wins
    // over Mo2InstanceDir (the userConfig install-dialog value / appsettings) — the runtime switch beats the install default.
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

    // The external-tool bridge (compile / BSA / log access): one resolver over the shared user config. Riders inject it.
    services.AddSingleton(new ToolPathResolver(store));

    // The Nexus Mods read bridge (QOL: answer Nexus questions directly instead of driving a browser). A typed HttpClient
    // so its timeout/lifetime are managed; KEYLESS (the public v2 GraphQL read surface needs no API key). This is
    // houseCARL's ONLY outbound network dependency — every failure is handled inside NexusClient (Q3), and the local
    // load-order tools never touch it, so they keep working with no internet.
    services.AddHttpClient<NexusClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL (+https://github.com/Avick3110/houseCARL)");
        // Nexus API Acceptable-Use Policy requires Application-Name + Application-Version on API traffic
        // (article 114). We send them on every request regardless of tier — cheap, compliant, and it identifies
        // houseCARL honestly to Nexus. The version is the exe's stamped release (ServerVersion), 0.0.0-dev unstamped.
        c.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
        c.DefaultRequestHeaders.Add("Application-Version", ServerVersion());
    });

    return (svc, explicitMode, instanceDir, instanceSource, configNote);
}

// The MCP server registration — server identity + instructions + the attribute-registered tools. ONLY the
// transport line differs between modes (the whole point of the stdio/http split); everything else is shared.
static void AddMcp(IServiceCollection services, bool stdio)
{
    var mcp = services.AddMcpServer(options =>
    {
        // The houseCARL brand string lives HERE — the one place in code (CLAUDE.md §6). The version is the exe's
        // stamped InformationalVersion: build-plugin.ps1 passes -p:Version from plugin.json (the single version
        // home), so ServerInfo reports the REAL release; an unstamped dev build honestly says 0.0.0-dev.
        options.ServerInfo = new Implementation { Name = "houseCARL", Version = ServerVersion() };
        options.ServerInstructions =
            "houseCARL exposes the Skyrim Special Edition load order at the data layer. Reads return the TRUE " +
            "load-order winner and, on request, the conflict tree; writes go to a NEW plugin, leaving originals " +
            "untouched. FormIDs are 'XXXXXX:Plugin.esp' (6 hex digits, then the defining master's filename). " +
            "Beyond the local load order, houseCARL reaches Nexus Mods directly and KEYLESSLY (no API key): " +
            "housecarl_nexus_search (find a mod by name); housecarl_nexus_mod (a mod's requirements, recommended INI " +
            "settings, accurate latest version and full page description, plus files=true for the complete per-file " +
            "list and changelog=true / since= for the per-version CHANGELOG and update delta, by id or URL); " +
            "housecarl_nexus_check_updates (batch FILE-LEVEL update check — pass each mod as 'id#fileid' to tell CURRENT " +
            "from OUTDATED for the EXACT file installed, correct for the multi-file pages where a version compare lies); and " +
            "housecarl_nexus_identify (trace a file to its source mod by MD5 hash). For mod updates, start with " +
            "housecarl_update_status — it reads MO2's OWN local update cache with NO network to narrow the list AND prints " +
            "each mod's 'id#fileid' verify token — then feed those tokens to housecarl_nexus_check_updates to confirm live. " +
            "Prefer these over a browser or generic web search for any Nexus lookup: " +
            "houseCARL can already read changelogs, file lists, and update status directly, so never hand-roll scripts " +
            "around a rendered Nexus page.";
    });
    // Stateless HTTP: each request is independent (no MCP session affinity); the resolver singleton persists across
    // requests regardless. Stdio is inherently a single long-lived session over the pipe.
    if (stdio) mcp.WithStdioServerTransport();
    else mcp.WithHttpTransport(o => o.Stateless = true);
    mcp.WithToolsFromAssembly();
    // The argument-binding shim (HCBR-2026-06-11-01): schema-driven coercion of obvious-intent argument shapes
    // (a bare string where an array is declared, quoted bools/numbers), named refusal of missing required
    // parameters, and a named rewrite of the SDK's generic binding-failure text. See ToolCallShim.
    mcp.WithRequestFilters(f => f.AddCallToolFilter(ToolCallShim.LenientArguments));
}

// The exe's stamped version for ServerInfo: InformationalVersion (set by build-plugin.ps1's -p:Version from
// plugin.json — ONE version home) with any "+metadata" suffix trimmed; an unstamped build reports 0.0.0-dev.
static string ServerVersion()
{
    var info = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : null;
    if (string.IsNullOrWhiteSpace(info)) return "0.0.0-dev";
    var plus = info.IndexOf('+');
    return plus > 0 ? info[..plus] : info;
}

using HousecarlMcp;
using ModelContextProtocol.Protocol;

// houseCARL MCP server. Stdio by default, --http for the localhost HTTP transport; either way it reads the active
// load order statically from the configured MO2 instance, and an empty config still boots.

bool useHttp = args.Contains("--http");
var hostArgs = args.Where(a => a != "--http").ToArray();   // strip our own flag so the config provider doesn't choke on it

// The optional cut on the published schemas, read before either host is built; a bad value stops the start here.
int? maxSchemaDepth;
try
{
    maxSchemaDepth = SchemaDepthCap.Configured();
}
catch (ArgumentException bad)
{
    Console.Error.WriteLine(bad.Message);
    return 1;
}

if (useHttp)
{
    var builder = WebApplication.CreateBuilder(hostArgs);
    var (svc, explicitMode, instanceDir, instanceSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: false, maxSchemaDepth);

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
    AddMcp(builder.Services, stdio: true, maxSchemaDepth);

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

return 0;   // the refusal above returns 1, so the exit code is spelled on both paths

// Shared setup — both transports call these, so the load order resolves identically either way.

// Loads the rulebook and applies the MO2-instance precedence: saved user config > explicit DataDir+ModsDir+ProfileDir
// > Mo2InstanceDir > unconfigured; builds and registers the LoadOrderService.
static (LoadOrderService svc, bool explicitMode, string? instanceDir, string instanceSource, string? configNote) SetupHouseCarl(IConfiguration config, IServiceCollection services)
{
    var cfg = config.GetSection("HouseCarl");

    var corpusPath = cfg["CorpusPath"];
    if (string.IsNullOrWhiteSpace(corpusPath))
        corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus.json");
    CorpusRulebook.CorpusPath = Path.GetFullPath(corpusPath);

    // user.json lives in HOUSECARL_DATA_DIR when set, else beside the exe; never under the plugin root, which the
    // client wipes on every plugin update.
    var pluginDataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
    var userConfigDir = string.IsNullOrWhiteSpace(pluginDataDir) ? AppContext.BaseDirectory : pluginDataDir;
    var userConfigPath = Path.Combine(userConfigDir, "houseCARL.user.json");
    // One owner of houseCARL.user.json, so the instance dir and the tool paths do not clobber each other.
    var store = new UserConfigStore(userConfigPath);
    services.AddSingleton(store);
    string? userInstanceDir = store.Load(out var configNote).Mo2InstanceDir;

    // The saved user config wins over Mo2InstanceDir: the runtime switch beats the install default.
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

    // The Nexus Mods read bridge: a typed, keyless HttpClient, and houseCARL's only outbound network dependency.
    services.AddHttpClient<NexusClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL (+https://github.com/Avick3110/houseCARL)");
        // The Nexus Acceptable-Use Policy requires these two headers on API traffic, so they ride every request.
        c.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
        c.DefaultRequestHeaders.Add("Application-Version", ServerVersion());
    });

    return (svc, explicitMode, instanceDir, instanceSource, configNote);
}

// The MCP server registration: identity, instructions, and the attribute-registered tools.
static void AddMcp(IServiceCollection services, bool stdio, int? maxSchemaDepth)
{
    var mcp = services.AddMcpServer(options =>
    {
        // The one place in code that carries the houseCARL brand string.
        options.ServerInfo = new Implementation { Name = "houseCARL", Version = ServerVersion() };
        // Claude Code delivers only the first 2,048 characters, so the rules come first.
        options.ServerInstructions =
            "houseCARL reads and writes a Skyrim SE load order via a live MO2 instance. " +
            "Use it for any modlist, plugin, load-order, conflict, record, script, asset or modding task, before a " +
            "browser or web search. " +
            "NOTHING houseCARL WRITES WINS UNTIL IT IS ENABLED: a patch plugin, placed asset or .seq waits " +
            "until the user enables its mod in MO2. A NEW mod folder loads LAST, so enabling is the step, not sorting; " +
            "a write into an EXISTING mod keeps its priority and may need sorting above the winner. Read-backs " +
            "describe the WRITTEN FILE, not the load order. " +
            "NEVER COPY GENERATED OUTPUT or build on it; its tool re-derives it: Requiem " +
            "for the Indifferent.esp, PGPatcher.esp, PG_1.esp, DynDOLOD.esm, DynDOLOD.esp, Occlusion.esp, a folder " +
            "holding NPC_Token.json or ParallaxGen_Diff.json, Synthesis, TexGen or xLODGen output. A mod that only " +
            "NAMES a generator (… Resources, … Fixes) is an INPUT: patch it normally. " +
            "RUNTIME DISTRIBUTION LAYERS go by what RECEIVES the change: a spell, perk, item, keyword, outfit or " +
            "faction onto NPCs is SPID, best BY GROUP; a keyword onto ITEM RECORDS is KID; a record's OWN FIELDS, " +
            "or an INDIVIDUAL NPC, is SkyPatcher, whose replayed layer " + ToolNames.SkypatcherLayer + " reads. " +
            "Read plugin contents and GMST/GLOB through " + ToolNames.Records + " first, never regex over bytes; a " +
            "setting no plugin defines has only its engine default, which houseCARL cannot show yet. " +
            "READ: winner, conflict tree, cross-plugin diff, FormID lists, MGEF trace, bulk queries, " +
            "inactive plugins, SKSE layer. " +
            "WRITE to a NEW plugin by default (in-place: opt-in, consent-gated): fields, leveled lists, containers, " +
            "conditions, new plugins/records/scripts, removals, forward or revert to vanilla, dialogue + validation. " +
            "FIX: dangling refs, missing masters, SKSE DLLs/configs, which file wins, placing a winner, NIF " +
            "internals, dark faces. " +
            "Also: ESL compact, merge, NPC appearance copy, Papyrus (de)compile, BSAs. " +
            "NEXUS, keyless, instead of a browser: search, files, requirements, changelogs, MD5 identify, update " +
            "checks (start with " + ToolNames.UpdateStatus + ", offline).";
    });
    // Stateless HTTP: each request is independent; the singletons persist across requests regardless.
    if (stdio) mcp.WithStdioServerTransport();
    else mcp.WithHttpTransport(o => o.Stateless = true);
    // Named, not implicit: the parameterless overload registers from the calling assembly.
    mcp.WithToolsFromAssembly(ToolSurface.Assembly);
    // The published-schema layer; see ToolSchemas.
    ToolSchemas.PublishSchemas(services, maxSchemaDepth);
    // The argument-binding shim; see ToolCallShim.
    mcp.WithRequestFilters(f => f.AddCallToolFilter(ToolCallShim.LenientArguments));
}

// The exe's stamped version for ServerInfo.
static string ServerVersion() => ServerBuild.Handshake;

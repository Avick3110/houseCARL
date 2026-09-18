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
            "MD5. Prefer over a browser or web search; each tool's own description carries the specifics. " +
            "RUNTIME DISTRIBUTION LAYERS — which framework owns a job is decided by what RECEIVES the change: " +
            "a spell, perk, item, keyword, outfit or faction onto NPCs is SPID, best BY GROUP (faction, race, " +
            "level, trait); a keyword onto ITEM RECORDS is KID; a record's OWN FIELDS, and an INDIVIDUAL NPC, are " +
            "SkyPatcher, whose replayed layer " + ToolNames.SkypatcherLayer + " reads. " +
            "NOTHING houseCARL WRITES WINS UNTIL IT IS ENABLED. A patch plugin, a placed asset and a written .seq " +
            "do nothing until the user enables that mod in MO2 — a NEW mod folder loads LAST, so enabling is the " +
            "step, not sorting, while a write into an EXISTING mod keeps that mod's priority and may still need " +
            "sorting above the current winner — and every read-back describes the WRITTEN FILE, not the load order. " +
            "NEVER COPY GENERATED OUTPUT: a tool re-derives it on its next run, so it is never copied into an " +
            "authored patch and never the base for authored work — 'Requiem for the Indifferent.esp'; " +
            "'PGPatcher.esp' / 'PG_1.esp'; 'DynDOLOD.esm' / 'DynDOLOD.esp' / 'Occlusion.esp'; a mod folder holding " +
            "'NPC_Token.json' or 'ParallaxGen_Diff.json'; a Synthesis, TexGen or xLODGen output folder. A mod that " +
            "only NAMES a generator ('… Resources', '… Fixes', a downloaded patch) is an INPUT it consumes, not " +
            "output: patch it normally.";
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

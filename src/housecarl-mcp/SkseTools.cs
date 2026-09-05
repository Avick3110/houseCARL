using System.ComponentModel;
using System.Text;
using System.Text.Json;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only view of the SKSE layer of the active load order: the .dll plugins under Data\SKSE\Plugins, the
/// configs beneath them, and the native Papyrus functions the order's compiled scripts declare. One tool, one finding
/// family per call — <c>findings=</c> picks inventory, pairing or config, and each family's render lives in its own
/// wire class below.
///
/// <para>The declared-versus-runtime ceiling is written ONCE, in the tool description, because it is the same ceiling
/// for all three families; per-family detail lives in the <c>findings=</c> parameter text. That is the whole reason
/// the three tools folded into one (SPEC §6.3 S7).</para>
///
/// <para>No response merges two families: the families answer different questions over different populations, and a
/// merged render would have no honest summary line. See <see cref="SksePluginReader"/>.</para></summary>
[McpServerToolType]
public static class SkseTools
{
    /// <summary>The three finding families <c>findings=</c> selects between.</summary>
    internal enum SkseFamily { Inventory, Pairing, Config }

    /// <summary>Parses <c>findings=</c> as it arrives OFF THE WIRE, where the schema declares it a string or an array.
    /// The array shape is the <c>housecarl_check</c> habit and is a real JSON array, not a string starting with '[':
    /// it binds so that the refusal below — which names the three families and the one-family rule — is what the caller
    /// reads, instead of the shim's generic type-mismatch sentence. Any non-string shape is refused by its own JSON
    /// text, so a one-element array is refused too: this tool runs one family per call, and taking <c>["inventory"]</c>
    /// as the scalar would teach a shape the tool description says is refused.</summary>
    internal static bool TryParseFamily(System.Text.Json.JsonElement? findings, out SkseFamily family, out string? error)
        => TryParseFamily(findings switch
        {
            null or { ValueKind: System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined } => null,
            { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
            { } el => el.GetRawText(),
        }, out family, out error);

    /// <summary>Parses <c>findings=</c>. Omitted is the inventory family, which every response states; an unknown
    /// value is refused naming the three, never quietly defaulted.</summary>
    internal static bool TryParseFamily(string? findings, out SkseFamily family, out string? error)
    {
        family = SkseFamily.Inventory;
        error = null;
        var token = (findings ?? "").Trim();
        if (token.Length == 0) return true;
        switch (token.ToLowerInvariant())
        {
            case "inventory": family = SkseFamily.Inventory; return true;
            case "pairing": family = SkseFamily.Pairing; return true;
            case "config": family = SkseFamily.Config; return true;
        }
        // A list-shaped value is the housecarl_check habit, where findings= names several families. Naming the shape is
        // what turns the refusal into a fix; "not a family" alone reads as the wrong word rather than the wrong shape.
        var shape = token.Contains(',') || token.Contains('[')
            ? " findings= here takes ONE value, not a list."
            : "";
        error = $"error: findings='{token}' is not a family on this tool — pass findings='inventory' (the DLL and config " +
                "layer), 'pairing' (native Papyrus declarations vs the DLLs that implement them) or 'config' (the form " +
                $"references SKSE configs declare vs your load order).{shape} One family per call.";
        return false;
    }

    /// <summary>The <c>peek=</c> family check, or null when the call is valid. peek= reads one DLL's image, which only
    /// the inventory family looks at, so it is refused on the other two rather than silently ignored.</summary>
    internal static string? PeekFamilyError(bool peek, SkseFamily family) =>
        peek && family != SkseFamily.Inventory
            ? $"error: peek= is the inventory family's — findings='{family.ToString().ToLowerInvariant()}' never reads a " +
              "DLL image. Drop peek=, or pass findings='inventory' with filter='<DLL/plugin/mod name>'."
            : null;

    /// <summary>The line every response ends on: which family ran, and the exact spelling for the two that did not.
    /// The default narrows only because the response says so.</summary>
    internal static string FamilyFooter(SkseFamily ran)
    {
        const string Inventory = "findings='inventory' (the DLL and config layer, with each plugin's static manifest)";
        const string Pairing = "findings='pairing' (native Papyrus declarations vs the DLLs that implement them)";
        const string Config = "findings='config' (the form references SKSE configs declare vs your load order)";
        var (mine, a, b) = ran switch
        {
            SkseFamily.Inventory => ("inventory", Pairing, Config),
            SkseFamily.Pairing => ("pairing", Inventory, Config),
            _ => ("config", Inventory, Pairing),
        };
        return $"\n\n(this call ran findings='{mine}'. NOT run: {a}; {b}.)";
    }

    /// <summary>The three family renders, one method each. The dispatch below picks one and appends the footer; behind
    /// this seam it can be driven without a live MO2 instance, so a test can pin which family a findings= value runs
    /// and that the answer ends on the footer. <see cref="ServiceRenders"/> is the live implementation.</summary>
    internal interface IFamilyRenders
    {
        string Inventory(FamilyCall c);
        string Pairing(FamilyCall c);
        string Config(FamilyCall c);
    }

    /// <summary>One call's shared render context: what to narrow to, the char budget, the TRANSPORT paging window,
    /// and which format was asked for. A record so a new TRANSPORT axis lands here rather than as a fourth positional
    /// argument on all three renders.</summary>
    internal readonly record struct FamilyCall(string? Filter, bool Peek, int Cap, RowWindow Window, bool Json);

    /// <summary>The live renders: each family's data read from the service, handed to its own wire class.</summary>
    sealed class ServiceRenders(LoadOrderService svc) : IFamilyRenders
    {
        public string Inventory(FamilyCall c)
        {
            var d = svc.SkseInventory(c.Peek ? c.Filter!.Trim() : null);
            return c.Json ? SkseInventoryWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : SkseInventoryWire.Render(d, c.Filter, c.Cap, c.Window);
        }

        public string Pairing(FamilyCall c)
        {
            var d = svc.NativePairingAudit();
            return c.Json ? NativePairingWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : NativePairingWire.Render(d, c.Filter, c.Cap, c.Window);
        }

        public string Config(FamilyCall c)
        {
            var d = svc.SkseConfigAudit();
            return c.Json ? SkseConfigAuditWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : SkseConfigAuditWire.Render(d, c.Filter, c.Cap, c.Window);
        }
    }

    /// <summary>Runs the selected family and appends the footer. The footer's length is subtracted from max_chars so it
    /// is paid for rather than added past the cap; the renders themselves test the cap BEFORE each list item and then
    /// append their scope note, caveats and filter hint unconditionally, so a response can still overrun a tight cap by
    /// one item plus that tail. Under a cap too small to hold both, the footer wins: it is the line that says which
    /// family answered.</summary>
    internal static string Dispatch(IFamilyRenders renders, SkseFamily family, string? filter, bool peek, int max_chars,
                                    bool json = false, RowWindow window = default)
    {
        // The json document states the family and the two that did not run in-band, so appending the text footer to
        // it would only break the document.
        var footer = json ? "" : FamilyFooter(family);
        int cap = Math.Max(1, (max_chars > 0 ? max_chars : 80_000) - footer.Length);
        var call = new FamilyCall(filter, peek, cap, window, json);
        var body = family switch
        {
            SkseFamily.Inventory => renders.Inventory(call),
            SkseFamily.Pairing => renders.Pairing(call),
            _ => renders.Config(call),
        };
        return body + footer;
    }

    /// <summary>The two families this call did not run, in the spelling that would run them — the json twin of
    /// <see cref="FamilyFooter"/>, so a json consumer learns the default narrowed exactly as a text one does.</summary>
    internal static string[] NotRun(SkseFamily ran) =>
        new[] { SkseFamily.Inventory, SkseFamily.Pairing, SkseFamily.Config }
            .Where(f => f != ran).Select(f => f.ToString().ToLowerInvariant()).ToArray();

    [McpServerTool(Name = ToolNames.Skse, ReadOnly = true, Title = "The SKSE layer: DLL/config inventory, native pairing, config references"),
     Description(
         // ---- what it is, and what selects a family ------------------------------------------------
         "The SKSE LAYER of the ACTIVE load order — the plane houseCARL's record and asset tools are blind to: the .dll " +
         "plugins under Data\\SKSE\\Plugins, every .ini/.toml/.json/.yaml config beneath them, and the native Papyrus " +
         "functions the order's compiled scripts declare. Read-only; writes nothing. ONE FAMILY PER CALL, selected by " +
         "findings= — 'inventory' (the DEFAULT when findings= is omitted), 'pairing' or 'config'. Each family's own " +
         "detail is on the findings= parameter below, and every response states which family it ran and the exact " +
         "spelling of the two it did not, so the default narrows only because the response says so. Two families are " +
         "never merged into one answer: they run over different populations and would share no honest summary line. " +
         // ---- the ceiling, once for all three -------------------------------------------------------
         "THE CEILING, stated once because it is the same for all three families: everything here is WHAT A FILE " +
         "DECLARES, NEVER WHAT THE DLL DOES. A version manifest, an import table, an embedded string and a config token " +
         "are static facts about a file on disk; loading, registering, hooking and reading are runtime behavior " +
         "houseCARL never observes, and the ABSENCE of a token proves nothing. So a finding here is a plausibility " +
         "verdict to VERIFY rather than a claim about a running game, and 'nothing found' is never a clean bill of " +
         "health. " +
         // ---- shared narrowing and the one boundary that spans every family -------------------------
         "filter= narrows whichever family ran (its match domain is that family's own — see filter= below); peek= " +
         "belongs to the inventory family alone and is refused, not ignored, on the other two. NOT COVERED by any " +
         "family: distributor INIs in Data\\ root (SPID *_DISTR, KID *_KID) — they live outside SKSE\\Plugins and are " +
         "owned by the spid-authoring / kid-authoring skills. " +
         // ---- transport, the same axes every tool carries -------------------------------------------
         "TRANSPORT: format= 'text' | 'json' (the same rows and accounting, machine-readable); limit=/offset= page " +
         "the family's row list; max_chars= caps the render. Every response ends on the in-band accounting — " +
         "total / rendered / skipped / capped / truncated / offset / remaining / notes — so what a window or a cap " +
         "left out is a number, never a silence.")]
    public static string Skse(
        LoadOrderService svc,
        [Description(
            "Optional. WHICH FAMILY to run — exactly one; the default when omitted is 'inventory'. ONE STRING, not a " +
            "list: unlike " + ToolNames.Check + "'s findings=, which names several families at once, this tool runs one " +
            "family per call, so findings=['inventory'] and findings='inventory,pairing' are both refused. " +
            // ---- family: inventory (harvested from housecarl_skse_inventory) -------------------------
            "'inventory' — the SKSE-plugin layer itself, over the FULL depth of Data\\SKSE\\Plugins: every .dll and " +
            "every .ini/.toml/.json/.yaml config beneath it, each with the MOD that wins the VFS for it. Configs are " +
            "grouped by their real subfolder (SkyPatcher, DynamicStringDistributor, OStim, … derived from the actual " +
            "tree, never a hardcoded list), so the default stays compact while accounting for everything; non-config " +
            "content is counted, never dropped. For every modern plugin it also reads the STATIC manifest the SKSE " +
            "loader itself reads — name, author, version, whether it uses Address Library (version-independent) or is " +
            "LOCKED to specific game runtimes, and the XSE floor — by parsing the DLL's SKSEPlugin_Version data export " +
            "WITHOUT loading or running it. Leads with the diagnostics: version-LOCKED plugins (won't load on a " +
            "mismatched game version), legacy query-only plugins (metadata set at runtime, not statically readable), " +
            "non-plugin DLLs (bundled dependencies), subfolder DLLs (not on SKSE's loader path), DLLs contested by more " +
            "than one mod, and DEBUG-BUILD plugins — a DLL importing the debug C runtime fails with error 126 for " +
            "anyone without Visual Studio, and it is flagged WITHOUT peek=. " +
            // ---- family: pairing (harvested from housecarl_native_pairing_audit) ---------------------
            "'pairing' — the declaration↔implementation seam, where 'a mod's scripts are installed but its DLL is " +
            "missing, won't load on this game version, or is 32-bit/BSA-packed/subfolder-shipped' hides. A native " +
            "function is ONE thing declared in TWO places (a .pex class with a native-flagged function, plus a DLL " +
            "registering the implementation at runtime); the halves ship as separate files and fail INDEPENDENTLY, and " +
            "the engine's response is a cryptic 'unable to bind' log plus calls that silently no-op. It scans the " +
            "winning copy of EVERY compiled script (loose + BSA), keeps the baseline honest by construction (a class " +
            "carried by an official archive is the ENGINE's — even when SKSE's loose override wins the file; skse64's " +
            "own script additions are SKSE CORE, implemented by the game-root loader), then pairs each remaining class " +
            "to the DLLs its provider mod — or a mod in its conflict chain, the bundling case — ships under " +
            "SKSE\\Plugins. Leads with PAIRED-BUT-DEAD (scripts installed and every candidate DLL statically will not " +
            "load: wrong game runtime for a version-LOCKED plugin, BSA-only, subfolder, 32-bit, unreadable, debug-built) " +
            "and UNPAIRED (no DLL in sight — a VERIFY flag, typically a declaration copy of a framework you don't have; " +
            "never called 'broken', because registration is runtime behavior). It answers 'is this pairing plausible and " +
            "healthy', NEVER 'does the DLL register exactly these functions'. " +
            // ---- family: config (harvested from housecarl_skse_config_audit) -------------------------
            "'config' — reference VALIDITY, so a BROKEN reference (a FormID pointing at a record that doesn't exist in " +
            "a plugin you DO have) is caught here instead of by a silent in-game failure, and kept apart from a merely " +
            "INERT one. It reads the WINNING copy of every .ini/.toml/.json/.yaml/.yml under the full depth of " +
            "Data\\SKSE\\Plugins (the copy the DLL actually reads) and extracts every form-shaped reference — a hex " +
            "FormID paired with a plugin filename in EITHER order (0xFORM|Plugin.esp as DSD/CDF/po3 write it, " +
            "Plugin.esp|0xFORM as SkyPatcher writes it, the ~ tilde form) plus plugin-named folder gates " +
            "(DynamicStringDistributor\\Plugin.esp\\...) — and resolves each against the real records of the active " +
            "order: OK, PLUGIN MISSING (plugin not in the order), DANGLING (plugin present but no such record) or " +
            "UNPARSEABLE (a shape-matched token that can't be normalized), summarized as BROKEN (dangling/unparseable, " +
            "actionable) vs INERT (plugin-missing, usually optional support for a mod you aren't running). The " +
            "framework-AGNOSTIC twin of the SkyPatcher reader's first half: it checks whether a reference RESOLVES, " +
            "never what it is FOR (per-framework skill territory). Extraction is a heuristic over token SHAPES, so a " +
            "token in a comment or a disabled block still surfaces; 'no references found' is the most common per-file " +
            "outcome and is accounted for, never a warning. Bare EditorID / name strings are NOT validated.")]
            // JsonElement, not string: the array shape must BIND so the refusal above answers it. The published type
            // is stamped string-or-array by ToolSchemas.ShapeUnionParams.
            System.Text.Json.JsonElement? findings = null,
        [Description(
            "Optional. A case-insensitive substring narrowing whichever family ran; the match domain is that family's " +
            "own. inventory: a plugin name, author, DLL filename, providing mod, or config FOLDER ('SkyPatcher', " +
            "'EngineFixes', 'po3', 'OStim') — expands that folder to its individual files, or shows one plugin in full " +
            "(all flags, compatible runtimes, email, providers, configs). pairing: a script CLASS name, providing mod, " +
            "paired mod, or DLL filename — full detail per matching class: the declared native function names, the " +
            "pairing evidence, each candidate DLL's manifest and load verdict, the conflict chains. config: a config " +
            "FOLDER, providing mod, filename, or REFERENCED-plugin name — audits just those configs and lists EVERY " +
            "reference with its verdict, the OKs included (positive confirmation of a patch you just authored). Omit " +
            "for the family's whole-layer view.")]
            string? filter = null,
        [Description(
            "Optional. The INVENTORY family only, and it REQUIRES filter=. Statically peeks inside the matching DLL's " +
            "IMAGE: the DLLs it imports (with derived flags — graphics/input hooks, network, and which sibling " +
            "non-plugin DLL is bundled for it), the config paths it embeds (which folder it actually scans), and the " +
            "plugin names it embeds, each cross-checked against your load order — the answer to 'what does this " +
            "unfamiliar DLL touch'. Per-DLL by design: it reads whole images, so a whole-layer peek is refused rather " +
            "than dumped. Passed with findings='pairing' or 'config' it is refused, never silently ignored.")]
            bool peek = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the machine-readable twin of whichever family ran — the same rows and the same accounting, in named fields).")]
            string? format = null,
        [Description("TRANSPORT: max rows to render from the family's row list — inventory: the DLLs (filter=: the DLL and config matches); pairing: the native-declaring classes; config: the config files. 0 = no limit. The census above the rows always states the whole layer, and the accounting line states what this window left out.")]
            int limit = 0,
        [Description("TRANSPORT: skip the first N rows of the family's row list, for paging a large layer. 0 = the beginning.")]
            int offset = 0,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.Skse, () =>
    {
        // The argument checks run BEFORE the config prompt: findings= is wrong in the same way whether or not an
        // instance is configured, and answering the prompt first would send the caller off to configure one only to
        // meet the same refusal.
        // format= is read first, because every refusal below has to be answered in the shape the caller asked for.
        // Its OWN refusal is the one that cannot be: a value that did not parse named no shape.
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        if (!TryParseFamily(findings, out var family, out var famErr)) return Wire.Refuse(json, famErr!);
        if (PeekFamilyError(peek, family) is { } peekErr) return Wire.Refuse(json, peekErr);
        if (SkseInventoryWire.PeekArgError(peek, filter) is { } err) return Wire.Refuse(json, err);
        var window = new RowWindow(offset, limit);
        if (window.Error is { } winErr) return Wire.Refuse(json, winErr);
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        return Dispatch(new ServiceRenders(svc), family, filter, peek, max_chars, json, window);
    });
}

/// <summary>Renders <see cref="SkseInventoryData"/>: summary and compat, the diagnostic subsets in full
/// (version-locked, legacy, non-plugin, subfolder, contested), the terse top-level plugin roster, then the config
/// folders grouped by count and provider. Everything is accounted for, bounded by max_chars with an explicit cut
/// notice. filter= expands a group to its individual configs, or a plugin to full detail.</summary>
static class SkseInventoryWire
{
    /// <summary>The <c>peek=</c> argument check, or null when the call is valid. A peek is per-DLL by design: peeking
    /// every DLL in the layer would read every image and render a wall that invites misreading noise as signal. So a
    /// bare <c>peek=true</c> fails rather than ignoring the flag or peeking one arbitrary DLL.</summary>
    internal static string? PeekArgError(bool peek, string? filter) =>
        peek && string.IsNullOrWhiteSpace(filter)
            ? "error: peek=true needs filter= — a peek is per-DLL, not a whole-layer dump (it reads each matching DLL's whole " +
              "image). Pass filter='<DLL/plugin/mod name>' to name the DLL to peek, e.g. filter='SkyPatcher' peek=true."
            : null;

    /// <summary>What this family's accounting counts: the DLL rows the whole-layer view lists. Configs are rendered
    /// as folder groups there, so they are stated in the census rather than paged as rows; the filter= view, whose
    /// population IS its matches, counts both and says so.</summary>
    internal const string RowNoun = "DLL(s)";
    internal const string MatchNoun = "match(es)";

    /// <summary>The DLL population split by loader scope and manifest kind. One function so the whole-layer census
    /// and the windowed row sections are computed the same way and cannot drift.</summary>
    readonly record struct DllSplit(List<SkseFileEntry> Loaded, List<SkseFileEntry> Subfolder, List<SkseFileEntry> Modern,
                                    List<SkseFileEntry> Legacy, List<SkseFileEntry> NotPlugin, List<SkseFileEntry> Unreadable,
                                    List<SkseFileEntry> BsaOnly, List<SkseFileEntry> Locked);

    static DllSplit Split(IReadOnlyList<SkseFileEntry> dlls)
    {
        // DLLs split by SKSE-loader scope: top-level DLLs are what SKSE loads; subfolder DLLs are seen but not loader-scoped.
        var loaded = dlls.Where(e => e.Group.Length == 0).ToList();
        var modern = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Modern }).ToList();
        return new DllSplit(
            loaded,
            dlls.Where(e => e.Group.Length > 0).ToList(),
            modern,
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.LegacyQuery }).ToList(),
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse }).ToList(),
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Unreadable }).ToList(),
            loaded.Where(e => e.Plugin is null).ToList(),
            modern.Where(e => !e.Plugin!.Version!.VersionIndependent).ToList());
    }

    public static string Render(SkseInventoryData d, string? filter, int cap, RowWindow window = default)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window);

        // The census states the WHOLE layer; limit=/offset= window only the rows listed below it. Room for the
        // accounting block is held back out of the cap so it is paid for rather than appended past it.
        var all = Split(d.Dlls);
        int notes = NoteCount(d);
        var rows = window.Apply(d.Dlls);
        int reserve = TransportAccounting.Reserve(d.Dlls.Count, rows.Count, window, notes, RowNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();

        var sb = new StringBuilder();
        var w = Split(rows);
        var loaded = w.Loaded; var subfolder = w.Subfolder; var modern = w.Modern; var legacy = w.Legacy;
        var notPlugin = w.NotPlugin; var unreadable = w.Unreadable; var bsaOnly = w.BsaOnly; var locked = w.Locked;

        int addrLib = all.Modern.Count(e => e.Plugin!.Version!.UsesAddressLibrary);
        int sig = all.Modern.Count(e => e.Plugin!.Version!.UsesSignatureScanning);
        int groupCount = d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        sb.Append("SKSE plugin layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(all.Loaded.Count).Append(" DLL(s), ").Append(d.Configs.Count).Append(" config(s) across ")
          .Append(groupCount).Append(" folder(s)");
        if (d.OtherFileCount > 0) sb.Append(", ").Append(d.OtherFileCount).Append(" other file(s)");
        sb.Append(" (full depth of SKSE\\Plugins)\n");
        sb.Append("plugins: ").Append(all.Modern.Count).Append(" with static metadata");
        if (all.Legacy.Count > 0) sb.Append(" · ").Append(all.Legacy.Count).Append(" legacy query-only");
        if (all.NotPlugin.Count > 0) sb.Append(" · ").Append(all.NotPlugin.Count).Append(" non-plugin (bundled deps)");
        if (all.BsaOnly.Count > 0) sb.Append(" · ").Append(all.BsaOnly.Count).Append(" BSA-only/unresolved");
        if (all.Unreadable.Count > 0) sb.Append(" · ").Append(all.Unreadable.Count).Append(" unreadable");
        if (all.Subfolder.Count > 0) sb.Append(" · ").Append(all.Subfolder.Count).Append(" in subfolders (not loader-scoped)");
        sb.Append('\n');
        sb.Append("compat: ").Append(addrLib).Append(" Address Library · ").Append(sig).Append(" signature-scanning · ")
          .Append(all.Locked.Count).Append(" version-LOCKED\n");

        // ── Diagnostic subsets, first and in full. ──

        // Debug-CRT offenders lead: the sharpest static verdict in the layer, deterministic breakage rather than a
        // mismatch to verify. Surfaced without peek=, because the import walk it needs rides the PE open that every
        // DLL's manifest read already pays for.
        var debugCrt = loaded.Where(x => x.Plugin is { Imports: not null } pl && pl.DebugCrtImports.Count > 0).ToList();
        if (debugCrt.Count > 0)
        {
            sb.Append("\n[!] DEBUG-BUILD plugins (").Append(debugCrt.Count)
              .Append(") — they import the debug C runtime, which ships only with Visual Studio and is NOT redistributable:\n");
            AppendCapped(sb, debugCrt, cap, x =>
            {
                var crt = x.Plugin!.DebugCrtImports;
                return $"  - {x.FileName} → needs {string.Join(", ", crt)}" +
                       $"{DebugCrtLayerVerdict(crt, SksePluginReader.IsSystemDllResolvable)}{Provider(x)}";
            }, tally);
        }

        if (locked.Count > 0)
        {
            // With the installed runtime resolved this is pass/fail per plugin; without it, the degrade is the
            // "verify each" wording.
            sb.Append("\n[!] version-LOCKED plugins (").Append(locked.Count)
              .Append(") — load ONLY on their listed runtime(s)");
            sb.Append(d.InstalledRuntime is { } rt0
                ? $"; installed game runtime is {rt0}:\n"
                : "; a mismatch with your game version = won't load:\n");
            AppendCapped(sb, locked, cap, e =>
            {
                var v = e.Plugin!.Version!;
                string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
                string verdict = d.InstalledRuntime is { } inst
                    ? (SksePluginReader.RuntimeCompatible(v, inst) ? "  = your game, loads" : "  ≠ your game — will NOT load")
                    : "";
                return $"  - {e.FileName} → {rt}{verdict}   [\"{v.Name}\"{Provider(e)}]";
            }, tally);
            if (d.InstalledRuntime is null)
            {
                var distinctRuntimes = locked.SelectMany(e => e.Plugin!.Version!.CompatibleVersions)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctRuntimes.Count > 1)
                    sb.Append("      ↑ these target DIFFERENT runtimes (").Append(string.Join(", ", distinctRuntimes))
                      .Append(") — verify each matches your game version (the installed version could not be resolved).\n");
            }
        }

        AppendSubset(sb, "legacy query-only (SE/VR-era — metadata set at runtime, not statically readable)", legacy, cap,
            e => $"  - {e.FileName}{Provider(e)}", tally);
        AppendSubset(sb, "non-plugin DLLs (no SKSE export — a bundled dependency, not a plugin)", notPlugin, cap,
            e => $"  - {e.FileName}{Provider(e)}", tally);
        AppendSubset(sb, "subfolder DLLs (present but NOT on SKSE's loader path — bundled/parent-loaded, not plugins SKSE loads)", subfolder, cap,
            e => $"  - {e.Group}\\{e.FileName}{Provider(e)}", tally);
        AppendSubset(sb, "BSA-only / unresolved DLLs (SKSE loads loose DLLs only — these will NOT load)", bsaOnly, cap,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Note}", tally);
        AppendSubset(sb, "unreadable DLLs (not a valid PE image)", unreadable, cap,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Plugin?.Note}", tally);

        var contested = loaded.Where(e => e.ProviderCount > 1).ToList();
        AppendSubset(sb, "contested DLLs (shipped by >1 mod — winner-first conflict chain; verify the winner is the one you want)", contested, cap,
            e => $"  - {e.FileName}: {Chain(e)}", tally);

        // ── Plugin roster: the loaded, metadata-bearing plugins, terse. ──
        sb.Append("\nplugins with metadata (").Append(modern.Count).Append(") — name · version · compat · winning mod:\n");
        AppendCapped(sb, modern.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(), cap, e =>
        {
            var v = e.Plugin!.Version!;
            return $"  - {e.FileName}  \"{v.Name}\" v{v.PluginVersion}  {CompatTag(v)}{Provider(e)}";
        }, tally);

        // ── Config folders, grouped by the derived subfolder and sorted by size. ──
        if (d.Configs.Count > 0)
        {
            var groups = d.Configs.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count(),
                              Providers: g.Select(e => e.WinningProvider).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                              Contested: g.Count(e => e.ProviderCount > 1)))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("\nconfig folders (").Append(groups.Count).Append(") — folder: files ← provider(s):\n");
            int shown = 0;
            foreach (var g in groups)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(groups.Count).Append(" folders; raise max_chars]\n"); break; }
                string prov = g.Providers.Count switch
                {
                    0 => "(no active provider)",
                    <= 2 => string.Join(", ", g.Providers),
                    _ => $"{g.Providers.Count} mods",
                };
                sb.Append("  - ").Append(g.Name).Append(": ").Append(g.Count).Append(" ← ").Append(prov);
                if (g.Contested > 0) sb.Append("  [").Append(g.Contested).Append(" contested]");
                sb.Append('\n'); shown++;
            }
        }

        sb.Append("(scope: full depth of Data\\SKSE\\Plugins. DLLs are top-level = what SKSE loads; configs at any depth are " +
                  "grouped by folder above. Non-config content (animation/mesh/etc.) is counted in the 'other file(s)' total.)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<plugin/mod/DLL name>' for a plugin's full detail, or filter='<folder>' (e.g. SkyPatcher, OStim) to list a config group.");
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Dlls.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>filter=: full detail for every matching DLL, then every matching config, matched by folder, filename or
    /// provider — so a folder name expands its group, a plugin name shows the manifest, and a mod name shows
    /// everything it provides.</summary>
    static string RenderFiltered(SkseInventoryData d, string filter, int cap, RowWindow window = default)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool MatchCfg(SkseFileEntry e) => In(e.FileName) || In(e.WinningProvider) || In(e.Group);

        // SkseFileEntry.MatchesDll is the one DLL predicate, so the service peeks exactly the entries this view renders.
        var allDllHits = d.Dlls.Where(e => e.MatchesDll(filter)).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var allCfgHits = d.Configs.Where(MatchCfg).ToList();

        // The filter's population is its matches, DLLs then configs, so the window walks the DLL matches first and
        // continues into the config matches. The header states the whole match count; the rows are the window.
        int total = allDllHits.Count + allCfgHits.Count;
        int notes = NoteCount(d);
        var dllHits = window.Apply(allDllHits);
        var cfgHits = window.After(allDllHits.Count, dllHits.Count).Apply(allCfgHits);
        int windowed = dllHits.Count + cfgHits.Count;
        int reserve = TransportAccounting.Reserve(total, windowed, window, notes, MatchNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(total, windowed, tally.Count, window, notes), MatchNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("SKSE plugin layer — filter '").Append(filter).Append("' — ")
          .Append(allDllHits.Count).Append(" DLL + ").Append(allCfgHits.Count).Append(" config match(es) [profile '").Append(d.ProfileName).Append("']\n");

        if (total == 0)
        {
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter,
                  d.Dlls.Select(e => e.FileName).Concat(d.Configs.Select(e => e.Group).Where(g => g.Length > 0)).Distinct()));
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        var shownCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in dllHits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [remaining DLL matches omitted at max_chars=").Append(cap).Append("]\n"); break; }
            AppendDetail(sb, e, d, shownCfg);
            tally.Mark(e.RelPath);
        }

        // peek= honoured with nothing to show is still an unanswered question. A bare peek=true fails in PeekArgError
        // and a matched-but-unpeekable DLL says so on its own entry, so this covers the last case: the filter matched
        // no DLL at all, leaving no entry to carry the notice.
        if (d.PeekRequested && allDllHits.Count == 0)
            sb.Append("\n[!] peek=true matched no DLL at all — nothing was peeked. Pass filter= the name of a loose DLL to peek it.\n");

        // Remaining matching configs (not already shown as a DLL's paired config), grouped by folder.
        var rest = cfgHits.Where(e => !shownCfg.Contains(e.RelPath))
            .OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (rest.Count > 0)
        {
            sb.Append("\nmatching configs (").Append(rest.Count).Append("):\n");
            string? curGroup = null;
            int shown = 0;
            foreach (var e in rest)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(rest.Count).Append("; raise max_chars]\n"); break; }
                string g = e.Group.Length == 0 ? "(top level)" : e.Group;
                if (g != curGroup) { sb.Append("  ").Append(g).Append(":\n"); curGroup = g; }
                sb.Append("    - ").Append(e.FileName);
                if (e.ProviderCount > 1) sb.Append(": ").Append(Chain(e));   // contested config → the full winner→loser chain
                else sb.Append(Provider(e));
                sb.Append('\n'); shown++; tally.Mark(e.RelPath);
            }
        }
        // A config already shown as a DLL's paired config is a rendered row too, so it counts.
        foreach (var e in cfgHits) if (shownCfg.Contains(e.RelPath)) tally.Mark(e.RelPath);
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    static void AppendDetail(StringBuilder sb, SkseFileEntry e, SkseInventoryData d, HashSet<string> shownCfg)
    {
        sb.Append('\n').Append(e.Group.Length > 0 ? e.Group + "\\" : "").Append(e.FileName).Append("  ← ")
          .Append(e.WinningProvider ?? "(no active provider)").Append(" (").Append(e.ProviderKind).Append(")\n");
        if (e.ProviderCount > 1)
            sb.Append("  [!] contested by ").Append(e.ProviderCount).Append(" mods — full chain (winner first): ").Append(Chain(e)).Append('\n');

        var p = e.Plugin;
        // Service-level note — subfolder-not-loader-scoped, no active provider, BSA-only — shown for any kind, since a
        // bundled-dependency or unreadable DLL in a subfolder also needs the loader-path flag.
        if (e.Note is { } enote) sb.Append("  [!] ").Append(enote).Append('\n');
        // A null Plugin is the BSA-only or unprovided DLL, which is exactly the entry a peek cannot read, so the peek
        // notice has to ride this branch too.
        if (p is null) { if (e.Note is null) sb.Append("  no static metadata\n"); AppendPeek(sb, e, d); return; }

        switch (p.Kind)
        {
            case SksePluginReader.SksePluginKind.LegacyQuery:
            case SksePluginReader.SksePluginKind.NotSkse:
            case SksePluginReader.SksePluginKind.Unreadable:
                sb.Append("  ").Append(p.Note).Append('\n');
                if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");
                // The import-table verdict rides every kind: a bundled dependency or an unreadable-manifest DLL still
                // has an import table, and a debug-CRT build often shows up as a DLL nobody can classify.
                AppendPeek(sb, e, d);
                return;   // Is64Bit == false is EXPLICITLY-determined non-x64; null (unknown) never triggers the claim (finding #1)
        }

        var v = p.Version!;
        sb.Append("  \"").Append(v.Name).Append("\" by ").Append(v.Author.Length > 0 ? v.Author : "(no author)");
        if (v.SupportEmail.Length > 0) sb.Append(" <").Append(v.SupportEmail).Append('>');
        sb.Append("\n  version ").Append(v.PluginVersion).Append('\n');
        if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");

        if (v.VersionIndependent)
        {
            var how = new List<string>();
            if (v.UsesAddressLibrary) how.Add("Address Library");
            if (v.UsesSignatureScanning) how.Add("signature scanning");
            sb.Append("  runtime compat: version-INDEPENDENT via ").Append(string.Join(" + ", how))
              .Append(" — loads on any supported game runtime");
            if (v.UsesAddressLibrary) sb.Append(" (needs the Address Library for SKSE Plugins mod installed)");
            sb.Append('\n');
        }
        else
        {
            string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed — will refuse every runtime!)";
            sb.Append("  runtime compat: version-LOCKED → loads ONLY on ").Append(rt)
              .Append("  [!] a game version outside this list = won't load\n");
        }
        var structs = new List<string>();
        if (v.UsesUpdatedStructs) structs.Add("post-1.6.629 structs");
        if (v.DeclaresNoStructs) structs.Add("no CommonLib structs");
        if (structs.Count > 0) sb.Append("  struct compat: ").Append(string.Join(", ", structs)).Append('\n');
        if (v.MinimumXseVersion is { } xse) sb.Append("  requires SKSE ≥ ").Append(xse).Append('\n');

        // Paired configs: any config under SKSE\Plugins whose basename stem matches the DLL. Best-effort association.
        string stem = System.IO.Path.GetFileNameWithoutExtension(e.FileName);
        var cfgs = d.Configs.Where(c => System.IO.Path.GetFileNameWithoutExtension(c.FileName)
            .StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cfgs.Count > 0)
        {
            sb.Append("  configs: ").Append(string.Join(", ", cfgs.Select(c =>
                (c.Group.Length > 0 ? c.Group + "\\" : "") + c.FileName + Provider(c)))).Append('\n');
            foreach (var c in cfgs) shownCfg.Add(c.RelPath);
        }
        AppendPeek(sb, e, d);
    }

    /// <summary>The peek block for one DLL: what the image statically contains — its imports with the derived flags,
    /// the config paths and plugin names it embeds, and the scan accounting. Renders nothing unless a peek ran. Every
    /// line is a fact about bytes in a file, and the framing line says so: it is not what the code does, and the
    /// absence of a string proves nothing, since plenty of DLLs build their references at runtime.</summary>
    static void AppendPeek(StringBuilder sb, SkseFileEntry e, SkseInventoryData d)
    {
        if (e.Peek is not { } peek)
        {
            // Per-entry, so a mixed match says it too: a filter hitting two loose DLLs and one BSA-only one must not
            // render two peeks and nothing at all for the third, which reads as an empty peek.
            if (d.PeekRequested)
                sb.Append("  (not peeked: no loose winner — SKSE loads loose DLLs only, so there is no image the game would read)\n");
            return;
        }
        sb.Append("  ── peek (what the image contains) ──\n");
        if (peek.Failed) { sb.Append("  [!] ").Append(peek.Note).Append('\n'); return; }

        // ── imports ──
        var imports = e.Plugin?.Imports;
        if (imports is null)
            sb.Append("  imports: UNKNOWN — the import directory could not be walked (corrupt or absent optional header)\n");
        else if (imports.Count == 0)
            sb.Append("  imports: none (walked, genuinely empty)\n");
        else
        {
            sb.Append("  imports (").Append(imports.Count).Append("): ").Append(string.Join(", ", imports)).Append('\n');
            var hooks = imports.Where(i => HookImports.ContainsKey(i)).ToList();
            foreach (var h in hooks) sb.Append("    → ").Append(h).Append(": ").Append(HookImports[h]).Append('\n');
            // Bundled-dependency attribution: an import satisfied by a sibling non-plugin DLL in the same layer, which
            // names why that stray DLL is installed.
            var siblings = d.Dlls.Where(x => x.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse })
                .Select(x => x.FileName).Where(f => imports.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (siblings.Count > 0)
                sb.Append("    → bundled with this plugin (a non-plugin DLL in this layer satisfies it): ")
                  .Append(string.Join(", ", siblings)).Append('\n');
        }
        AppendDebugCrt(sb, e);

        // ── config surface ──
        if (peek.ConfigPaths.Count > 0)
        {
            sb.Append("  config paths embedded (").Append(peek.ConfigPaths.Count).Append("):\n");
            foreach (var c in peek.ConfigPaths.Take(PeekListCap)) sb.Append("    - ").Append(c).Append('\n');
            if (peek.ConfigPaths.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.ConfigPaths.Count).Append("]\n");
        }

        // ── plugin references, cross-checked ──
        if (peek.PluginRefs.Count > 0)
        {
            sb.Append("  plugin names embedded (").Append(peek.PluginRefs.Count).Append("):\n");
            foreach (var r in peek.PluginRefs.Take(PeekListCap))
            {
                string verdict = d.ActivePlugins is null ? ""
                    : d.ActivePlugins.Contains(r) ? "  (in your load order)"
                    : "  [!] NOT in your load order";
                sb.Append("    - ").Append(r).Append(verdict).Append('\n');
            }
            if (peek.PluginRefs.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.PluginRefs.Count).Append("]\n");
        }

        sb.Append("  scanned ").Append(peek.RunsScanned).Append(" string run(s) over ")
          .Append(peek.BytesScanned / 1024).Append(" KB → showed ")
          .Append(peek.ConfigPaths.Count + peek.PluginRefs.Count)
          .Append(" (the classes above are a FILTER over the image, not the whole haystack)\n");
        sb.Append("  (imports/strings are what the image CONTAINS, never what the code DOES — behavior is unreadable by " +
                  "design. Absence proves nothing: many DLLs build their references at runtime or read them from configs.)\n");
    }

    /// <summary>Max entries per peek list before an explicit cut: a peek is per-DLL and readability is the point,
    /// since noise here is easily misread as signal.</summary>
    const int PeekListCap = 40;

    /// <summary>Imports whose presence names a capability the DLL reaches for: facts about the import table with a
    /// plain gloss, never a behaviour claim. It hooks the API; what it does with it is not visible here.</summary>
    static readonly Dictionary<string, string> HookImports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d3d11.dll"] = "Direct3D 11 — touches graphics/rendering",
        ["dxgi.dll"] = "DXGI — touches the swapchain/presentation layer",
        ["d3dcompiler_47.dll"] = "D3D shader compiler — compiles shaders at runtime",
        ["dinput8.dll"] = "DirectInput — touches input handling",
        ["xinput1_3.dll"] = "XInput — touches controller input",
        ["ws2_32.dll"] = "Winsock — opens network sockets",
        ["winhttp.dll"] = "WinHTTP — makes HTTP requests",
        ["wininet.dll"] = "WinINet — makes internet requests",
    };

    /// <summary>The Debug-CRT verdict — the one peek line allowed "will not load" language, because it is a static,
    /// deterministic loader fact. The debug CRT is not redistributable: it ships with Visual Studio and is absent from
    /// a stock Windows, so a plugin importing it dies with error 126. That is only unconditionally true where the
    /// runtime is absent, and this runs on the modder's own machine, so it checks rather than assumes: absent here
    /// means it will not load, stated flatly; present here means it loads for you and is broken for everyone
    /// else.</summary>
    static void AppendDebugCrt(StringBuilder sb, SkseFileEntry e)
    {
        if (e.Plugin is not { Imports: not null } p) return;      // never walked ⇒ no claim either way
        var crt = p.DebugCrtImports;
        if (crt.Count == 0) return;
        sb.Append(DebugCrtVerdict(crt, SksePluginReader.IsSystemDllResolvable));
    }

    /// <summary>The one-line Debug-CRT verdict for the whole-layer summary: the same machine-dependence as
    /// <see cref="DebugCrtVerdict"/>, in the terse register the roster needs. The probe is injected rather than called
    /// inline so both wordings are reachable regardless of the current machine.</summary>
    internal static string DebugCrtLayerVerdict(IReadOnlyList<string> crt, Func<string, bool> resolvable) =>
        crt.All(resolvable)
            ? "  loads on THIS machine (you have the debug runtime) — but error 126 for anyone without Visual Studio"
            : "  ≠ this machine — will NOT load (error 126: the debug runtime isn't here)";

    /// <summary>The Debug-CRT verdict text, pure with the machine probe injected so both wordings are reachable in one
    /// run: called inline, a machine without Visual Studio could only ever produce the "will NOT load" wording and a
    /// dev box only the other.</summary>
    internal static string DebugCrtVerdict(IReadOnlyList<string> crt, Func<string, bool> resolvable)
    {
        var missing = crt.Where(c => !resolvable(c)).ToList();
        var sb = new StringBuilder();
        sb.Append("  [!] DEBUG BUILD — imports the debug C runtime: ").Append(string.Join(", ", crt)).Append('\n');
        if (missing.Count > 0)
            sb.Append("      → will NOT load: ").Append(string.Join(", ", missing))
              .Append(missing.Count == 1 ? " is" : " are").Append(" not present on this machine, so the loader fails with " +
                      "error 126 (ERROR_MOD_NOT_FOUND). The debug CRT ships only with Visual Studio and is not redistributable — " +
                      "this DLL was shipped as a Debug build by mistake. Ask its author for a Release build.\n");
        else
            sb.Append("      → it loads on THIS machine (you have the debug runtime installed — Visual Studio), but it will " +
                      "fail with error 126 for anyone who doesn't. If you built this, ship a Release build.\n");
        return sb.ToString();
    }

    /// <summary>The compat one-word tag for the terse roster: "AddrLib", "SigScan", or "LOCKED→[runtimes]".</summary>
    static string CompatTag(SksePluginReader.SkseVersionInfo v)
    {
        if (v.UsesAddressLibrary) return "AddrLib";
        if (v.UsesSignatureScanning) return "SigScan";
        return "LOCKED→" + (v.CompatibleVersions.Count > 0 ? string.Join("/", v.CompatibleVersions) : "?");
    }

    static string Provider(SkseFileEntry e) =>
        e.WinningProvider is null ? "  (no active provider)" : $"  ← {e.WinningProvider}";

    /// <summary>The full VFS conflict chain: winner first, then losers in precedence order, each tagged loose or BSA —
    /// which mod wins this file and who it overrides. "(no active provider)" when empty.</summary>
    static string Chain(SkseFileEntry e) =>
        e.Providers.Count == 0 ? "(no active provider)" : string.Join(" › ", e.Providers.Select(p => $"{p.Name} ({p.Kind})"));

    static void AppendSubset(StringBuilder sb, string label, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line,
                             RowTally? tally = null)
    {
        if (items.Count == 0) return;
        sb.Append('\n').Append(label).Append(" (").Append(items.Count).Append("):\n");
        AppendCapped(sb, items, cap, line, tally);
    }

    static void AppendCapped(StringBuilder sb, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line,
                             RowTally? tally = null)
    {
        int shown = 0;
        foreach (var e in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter= to see all]\n"); break; }
            sb.Append(line(e)).Append('\n'); shown++; tally?.Mark(e.RelPath);
        }
    }

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count, and the
    /// same three the caveat block renders.</summary>
    internal static int NoteCount(SkseInventoryData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count;

    /// <summary>The json twin of <see cref="Render"/>: the same census, the same windowed rows, the same accounting,
    /// in named fields. Rows are dropped from the tail when the document reaches max_chars — never a cut of the
    /// serialized string, which would emit malformed json — and the accounting says how many that was.</summary>
    public static string RenderJson(SkseInventoryData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allDlls = filtered ? d.Dlls.Where(e => e.MatchesDll(f)).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                               : d.Dlls.ToList();
        var allCfgs = filtered ? d.Configs.Where(e => In(e.FileName) || In(e.WinningProvider) || In(e.Group)).ToList()
                               : new List<SkseFileEntry>();
        int total = allDlls.Count + allCfgs.Count;
        var dlls = window.Apply(allDlls);
        var cfgs = window.After(allDlls.Count, dlls.Count).Apply(allCfgs);
        int windowed = dlls.Count + cfgs.Count;
        var all = Split(d.Dlls);
        int notes = NoteCount(d);
        int rendered = 0;

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Inventory, filter, d.ProfileName, (w, ms) =>
        {
            SkseJsonDoc.Nullable(w, "installed_runtime", d.InstalledRuntime);
            w.WriteStartObject("totals");
            w.WriteNumber("dlls", all.Loaded.Count);
            w.WriteNumber("subfolder_dlls", all.Subfolder.Count);
            w.WriteNumber("configs", d.Configs.Count);
            w.WriteNumber("config_folders", d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            w.WriteNumber("other_files", d.OtherFileCount);
            w.WriteNumber("modern", all.Modern.Count);
            w.WriteNumber("legacy_query", all.Legacy.Count);
            w.WriteNumber("non_plugin", all.NotPlugin.Count);
            w.WriteNumber("bsa_only", all.BsaOnly.Count);
            w.WriteNumber("unreadable", all.Unreadable.Count);
            w.WriteNumber("address_library", all.Modern.Count(e => e.Plugin!.Version!.UsesAddressLibrary));
            w.WriteNumber("signature_scanning", all.Modern.Count(e => e.Plugin!.Version!.UsesSignatureScanning));
            w.WriteNumber("version_locked", all.Locked.Count);
            w.WriteEndObject();

            w.WriteStartArray("dlls");
            foreach (var e in dlls)
            {
                if (SkseJsonDoc.Over(w, ms, cap)) break;
                WriteDllJson(w, e, d);
                rendered++;
            }
            w.WriteEndArray();

            w.WriteStartArray("configs");
            foreach (var e in cfgs)
            {
                if (SkseJsonDoc.Over(w, ms, cap)) break;
                WriteConfigFileJson(w, e);
                rendered++;
            }
            w.WriteEndArray();

            // The whole-layer view groups configs by folder rather than listing them; the twin states the same table.
            w.WriteStartArray("config_folders");
            if (!filtered)
                foreach (var g in d.Configs.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                             .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count(),
                                           Providers: g.Select(e => e.WinningProvider).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                           Contested: g.Count(e => e.ProviderCount > 1)))
                             .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (SkseJsonDoc.Over(w, ms, cap)) break;
                    w.WriteStartObject();
                    w.WriteString("folder", g.Name);
                    w.WriteNumber("files", g.Count);
                    SkseJsonDoc.Strings(w, "providers", g.Providers!);
                    w.WriteNumber("contested", g.Contested);
                    w.WriteEndObject();
                }
            w.WriteEndArray();

            if (d.PeekRequested && allDlls.Count == 0)
                w.WriteString("peek_note", "peek=true matched no DLL at all — nothing was peeked.");
            SkseJsonDoc.Caveats(w, d.ReadIncomplete, d.Warnings, d.BsaFailures);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(total, windowed, rendered, window, notes));
        });
    }

    static void WriteDllJson(Utf8JsonWriter w, SkseFileEntry e, SkseInventoryData d)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", e.RelPath);
        w.WriteString("file_name", e.FileName);
        w.WriteString("group", e.Group);
        w.WriteBoolean("loader_scoped", e.Group.Length == 0);
        SkseJsonDoc.Nullable(w, "winning_provider", e.WinningProvider);
        w.WriteString("provider_kind", e.ProviderKind);
        w.WriteNumber("provider_count", e.ProviderCount);
        SkseJsonDoc.Providers(w, e.Providers);
        SkseJsonDoc.Nullable(w, "note", e.Note);
        var p = e.Plugin;
        SkseJsonDoc.Nullable(w, "kind", p is null ? null : p.Kind.ToString().ToLowerInvariant());
        if (p?.Is64Bit is { } bits) w.WriteBoolean("is_64bit", bits); else w.WriteNull("is_64bit");
        SkseJsonDoc.Strings(w, "debug_crt_imports", p?.DebugCrtImports ?? Array.Empty<string>());
        // Null, not [], when the import walk never ran or failed: absence of evidence is not evidence of absence.
        if (p?.Imports is { } imports) SkseJsonDoc.Strings(w, "imports", imports); else w.WriteNull("imports");
        if (p?.Version is { } v)
        {
            w.WriteStartObject("version");
            w.WriteString("name", v.Name);
            w.WriteString("author", v.Author);
            w.WriteString("support_email", v.SupportEmail);
            w.WriteString("plugin_version", v.PluginVersion);
            w.WriteBoolean("version_independent", v.VersionIndependent);
            w.WriteBoolean("uses_address_library", v.UsesAddressLibrary);
            w.WriteBoolean("uses_signature_scanning", v.UsesSignatureScanning);
            w.WriteBoolean("uses_updated_structs", v.UsesUpdatedStructs);
            w.WriteBoolean("declares_no_structs", v.DeclaresNoStructs);
            SkseJsonDoc.Strings(w, "compatible_versions", v.CompatibleVersions);
            SkseJsonDoc.Nullable(w, "minimum_xse_version", v.MinimumXseVersion);
            if (d.InstalledRuntime is { } rt && !v.VersionIndependent)
                w.WriteBoolean("loads_on_installed_runtime", SksePluginReader.RuntimeCompatible(v, rt));
            w.WriteEndObject();
        }
        else w.WriteNull("version");
        if (e.Peek is { } peek)
        {
            w.WriteStartObject("peek");
            SkseJsonDoc.Strings(w, "config_paths", peek.ConfigPaths);
            SkseJsonDoc.Strings(w, "plugin_refs", peek.PluginRefs);
            w.WriteNumber("runs_scanned", peek.RunsScanned);
            w.WriteNumber("bytes_scanned", peek.BytesScanned);
            SkseJsonDoc.Nullable(w, "note", peek.Note);
            w.WriteEndObject();
        }
        else w.WriteNull("peek");
        w.WriteEndObject();
    }

    static void WriteConfigFileJson(Utf8JsonWriter w, SkseFileEntry e)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", e.RelPath);
        w.WriteString("file_name", e.FileName);
        w.WriteString("group", e.Group);
        SkseJsonDoc.Nullable(w, "winning_provider", e.WinningProvider);
        w.WriteString("provider_kind", e.ProviderKind);
        w.WriteNumber("provider_count", e.ProviderCount);
        SkseJsonDoc.Providers(w, e.Providers);
        w.WriteEndObject();
    }

    static void AppendCaveats(StringBuilder sb, SkseInventoryData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a file present only in it may be missing from this inventory (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}

/// <summary>Renders <see cref="SkseConfigAuditData"/>. The health summary separates broken references — DANGLING and
/// UNPARSEABLE, which should resolve and do not — from inert ones, PLUGIN MISSING, where the named plugin simply is
/// not installed. Then the diagnostics in full, each with file:line provenance and its winning provider, then the
/// accounted-for remainder: healthy files counted, and no-reference files grouped by folder. Bounded by max_chars with
/// an explicit cut notice. filter= audits one group and lists every reference with its verdict, OKs included.</summary>
static class SkseConfigAuditWire
{
    // A dead reference and the file it was declared in.
    readonly record struct Hit(SkseConfigFileAudit File, SkseAuditedRef Audited)
    {
        public HousecarlCore.SkseConfigRef Ref => Audited.Ref;
    }

    /// <summary>What this family's accounting counts: the config FILES the audit covers.</summary>
    internal const string RowNoun = "config(s)";

    public static string Render(SkseConfigAuditData d, string? filter, int cap, RowWindow window = default)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window);

        // Every count below states the WHOLE audit; limit=/offset= window only the files the sections LIST. Room for
        // the accounting block is held back out of the cap so it is paid for rather than appended past it.
        int notes = NoteCount(d);
        var rows = window.Apply(d.Files);
        int reserve = TransportAccounting.Reserve(d.Files.Count, rows.Count, window, notes, RowNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();

        var flatAll = d.Files.SelectMany(f => f.Refs.Select(r => new Hit(f, r))).ToList();
        var flat = rows.SelectMany(f => f.Refs.Select(r => new Hit(f, r))).ToList();
        var missingGates = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate).ToList();
        var missingToks  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.FormToken).ToList();
        var dangling     = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Dangling).ToList();
        var unparseable  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Unparseable).ToList();
        var readErrors   = rows.Where(f => f.ReadError is not null).ToList();

        int Count(Func<Hit, bool> p) => flatAll.Count(p);
        int danglingAll    = Count(h => h.Audited.Verdict == SkseRefVerdict.Dangling);
        int unparseableAll = Count(h => h.Audited.Verdict == SkseRefVerdict.Unparseable);
        int inertAll       = Count(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing);

        int refsChecked = flatAll.Count;
        // Two distinct signals, kept apart in the headline. BROKEN is a reference that should resolve and does not —
        // DANGLING (plugin present, record absent) or UNPARSEABLE (an unreadable token) — and is the actionable one.
        // INERT is PLUGIN MISSING, gate or token: the named plugin is not installed, so the entry does nothing. For a
        // config shipping optional support for a mod you do not have that is expected, not a fault, and counting it as
        // dead would make a healthy order read as thousands of dead references.
        int broken = danglingAll + unparseableAll;
        int inert  = inertAll;
        int notOk  = broken + inert;                       // every non-OK ref (kept for the accounted-for reconciliation below)
        int filesWithRefs = d.Files.Count(f => f.Refs.Count > 0);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.ConfigCount).Append(" config(s) scanned, ").Append(filesWithRefs).Append(" carry references, ")
          .Append(refsChecked).Append(" reference(s) checked\n");
        if (broken == 0 && inert == 0)
            sb.Append("✓ every reference resolves against the active load order — nothing broken, nothing inert.\n");
        else if (broken == 0)
            sb.Append("✓ no broken references — every reference to an installed plugin resolves. (")
              .Append(inert).Append(" reference(s) point at plugins not in your load order — inert, usually optional support for a mod you don't have.)\n");
        else
        {
            sb.Append("[!] ").Append(broken).Append(" BROKEN reference(s): ")
              .Append(danglingAll).Append(" dangling · ").Append(unparseableAll).Append(" unparseable");
            if (inert > 0)
                sb.Append("   ·   ").Append(inert).Append(" more inert (plugin not installed — usually optional support)");
            sb.Append('\n');
        }

        // ── Diagnostics first, in full. ──
        AppendHits(sb, "PLUGIN MISSING — folder gates (the plugin isn't installed, so the WHOLE file is inert)", missingGates, cap,
            h => $"  - {h.File.RelPath}: folder '{h.Ref.Plugin}' not in the load order{Prov(h.File)}", tally);
        // Token-level plugin-missing is grouped by the target plugin: a whole-layer scan yields tens of thousands of
        // individual inert refs, so a per-ref list is an unreadable wall and the count-per-plugin table is the
        // actionable shape. filter= a plugin to see its individual refs.
        if (missingToks.Count > 0)
        {
            var byPlugin = missingToks.GroupBy(h => h.Ref.Plugin, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Plugin: g.Key, Refs: g.Count(),
                              Files: g.Select(h => h.File.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                              Example: g.First().File.RelPath))
                .OrderByDescending(g => g.Refs).ThenBy(g => g.Plugin, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("\nPLUGIN MISSING — target plugin not in the load order (inert; often a config shipping optional support for a mod you don't have) — by plugin (")
              .Append(byPlugin.Count).Append(" plugins, ").Append(missingToks.Count).Append(" refs):\n");
            int shown = 0;
            foreach (var g in byPlugin)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(byPlugin.Count).Append(" plugins; raise max_chars or use filter=]\n"); break; }
                sb.Append("  - ").Append(g.Plugin).Append(": ").Append(g.Refs).Append(" ref(s)");
                if (g.Files.Count > 1) sb.Append(" across ").Append(g.Files.Count).Append(" file(s)");
                sb.Append("  (e.g. ").Append(g.Example).Append(")\n"); shown++;
                foreach (var f in g.Files) tally.Mark(f);
            }
        }
        AppendHits(sb, "DANGLING — plugin present but no such record (a dead reference)", dangling, cap,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}", tally);
        AppendHits(sb, "UNPARSEABLE — shape-matched tokens that can't be normalized (flagged, never guessed)", unparseable, cap,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}", tally);
        if (readErrors.Count > 0)
        {
            sb.Append("\nread errors — configs that could not be read/decoded (NOT counted as clean) (").Append(readErrors.Count).Append("):\n");
            int shown = 0;
            foreach (var f in readErrors)
            {
                if (sb.Length >= cap) { sb.Append("  ... [").Append(shown).Append(" of ").Append(readErrors.Count).Append("; raise max_chars]\n"); break; }
                sb.Append("  - ").Append(f.RelPath).Append(": ").Append(f.ReadError).Append(Prov(f)).Append('\n'); shown++; tally.Mark(f.RelPath);
            }
        }

        // ── Accounted-for remainder: everything that is not a diagnostic, so nothing is dropped. ──
        var healthyFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count > 0 && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)).ToList();
        int healthyRefs = healthyFiles.Sum(f => f.Refs.Count);
        int okInMixed = (refsChecked - notOk) - healthyRefs;   // OK refs living in a file that ALSO has a non-OK ref — so every ref reconciles: refsChecked = notOk + healthyRefs + okInMixed
        var noRefFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count == 0).ToList();
        // A clean file is accounted for by the count line above, not by a row of its own, so it counts as rendered.
        foreach (var f in rows)
            if (f.ReadError is null && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)) tally.Mark(f.RelPath);
        sb.Append("\naccounted for: ").Append(healthyFiles.Count).Append(" file(s) with ").Append(healthyRefs)
          .Append(" reference(s) all OK");
        if (okInMixed > 0) sb.Append(" · ").Append(okInMixed).Append(" more OK ref(s) in files that also carry a non-OK reference");
        sb.Append(" · ").Append(noRefFiles.Count).Append(" file(s) declare no form-shaped references\n");
        if (noRefFiles.Count > 0)
        {
            var groups = noRefFiles.GroupBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("  no-reference configs by folder (").Append(groups.Count).Append("):\n");
            int shown = 0;
            foreach (var g in groups)
            {
                if (sb.Length >= cap) { sb.Append("    ... [").Append(shown).Append(" of ").Append(groups.Count).Append(" folders; raise max_chars]\n"); break; }
                sb.Append("    - ").Append(g.Name).Append(": ").Append(g.Count).Append('\n'); shown++;
            }
        }

        sb.Append("\n(scope: form-shaped references only — a hex FormID + plugin filename, or a plugin-named folder gate. Bare " +
                  "EditorID/name strings are not validated (Wave 2). Extraction is heuristic over token shapes: a token in a comment " +
                  "or disabled block still counts — 'references this file declares', not 'the DLL will use'. A folder that SHOULD carry " +
                  "references but shows none may use a reference shape not yet recognized.)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<folder/mod/filename/plugin>' to audit one group and see every reference (OKs included).");
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Files.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>filter=: audit just the matching configs — by folder, provider, filename, or a referenced plugin — and
    /// list every reference with its verdict, OKs included, so the view also serves as positive confirmation.</summary>
    static string RenderFiltered(SkseConfigAuditData d, string filter, int cap, RowWindow window = default)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(SkseConfigFileAudit f) =>
            In(f.FileName) || In(f.Group) || In(f.WinningProvider) || In(f.RelPath)
            || f.Refs.Any(r => In(r.Ref.Plugin));
        var allHits = d.Files.Where(Match)
            .OrderBy(f => f.Group, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList();

        int notes = NoteCount(d);
        var hits = window.Apply(allHits);
        int reserve = TransportAccounting.Reserve(allHits.Count, hits.Count, window, notes, RowNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(allHits.Count, hits.Count, tally.Count, window, notes), RowNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — filter '").Append(filter).Append("' — ")
          .Append(allHits.Count).Append(" config(s) match [profile '").Append(d.ProfileName).Append("']\n");
        if (allHits.Count == 0)
        {
            // The suggestion pool must span every axis Match filters on, or a mistyped plugin or provider filter gets
            // only folder and filename suggestions. Match keys on filename, group, provider, relpath and
            // referenced-plugin, so the pool carries all five. PluginNameSuggest dedups and skips empties.
            var suggestPool = d.Files.Select(f => f.FileName)
                .Concat(d.Files.Select(f => f.Group).Where(g => g.Length > 0))
                .Concat(d.Files.Select(f => f.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Files.SelectMany(f => f.Refs.Select(r => r.Ref.Plugin)));
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, suggestPool));
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        int shownFiles = 0;
        foreach (var f in hits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [showing ").Append(shownFiles).Append(" of ").Append(hits.Count).Append(" files; raise max_chars]\n"); break; }
            sb.Append('\n').Append(f.RelPath).Append("  ← ").Append(f.WinningProvider ?? "(no active provider)").Append('\n');
            if (f.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(f.ProviderCount).Append(" mods (winner audited): ")
                  .Append(string.Join(" › ", f.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            if (f.ReadError is not null) { sb.Append("  [!] ").Append(f.ReadError).Append('\n'); tally.Mark(f.RelPath); continue; }
            if (f.Refs.Count == 0) { sb.Append("  (no form-shaped references)\n"); tally.Mark(f.RelPath); continue; }
            foreach (var r in f.Refs)
                sb.Append("  ").Append(Tag(r.Verdict)).Append(' ')
                  .Append(r.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate ? $"folder gate '{r.Ref.Plugin}'" : $"'{r.Ref.Raw}'")
                  .Append(r.Ref.Line > 0 ? $" (line {r.Ref.Line})" : "")
                  .Append(r.Detail is null ? "" : " → " + r.Detail).Append('\n');
            shownFiles++; tally.Mark(f.RelPath);
        }
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    static string Tag(SkseRefVerdict v) => v switch
    {
        SkseRefVerdict.Ok => "[OK]",
        SkseRefVerdict.PluginMissing => "[MISSING]",
        SkseRefVerdict.Dangling => "[DANGLING]",
        SkseRefVerdict.Unparseable => "[UNPARSEABLE]",
        _ => "[?]",
    };

    static string Loc(Hit h) => h.Ref.Line > 0 ? $"{h.File.RelPath}:{h.Ref.Line}" : h.File.RelPath;
    static string Prov(SkseConfigFileAudit f) => f.WinningProvider is null ? "" : $"  [← {f.WinningProvider}]";

    static void AppendHits(StringBuilder sb, string label, IReadOnlyList<Hit> items, int cap, Func<Hit, string> line,
                           RowTally? tally = null)
    {
        if (items.Count == 0) return;
        sb.Append('\n').Append(label).Append(" (").Append(items.Count).Append("):\n");
        int shown = 0;
        foreach (var h in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter=]\n"); break; }
            sb.Append(line(h)).Append('\n'); shown++; tally?.Mark(h.File.RelPath);
        }
    }

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count.</summary>
    internal static int NoteCount(SkseConfigAuditData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count;

    /// <summary>The json twin of <see cref="Render"/>: the same census, the same windowed files with every reference
    /// and its verdict, and the same accounting, in named fields.</summary>
    public static string RenderJson(SkseConfigAuditData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allFiles = filtered
            ? d.Files.Where(x => In(x.FileName) || In(x.Group) || In(x.WinningProvider) || In(x.RelPath) || x.Refs.Any(r => In(r.Ref.Plugin)))
                     .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RelPath, StringComparer.OrdinalIgnoreCase).ToList()
            : d.Files.ToList();
        var files = window.Apply(allFiles);
        var flatAll = d.Files.SelectMany(x => x.Refs).ToList();
        int notes = NoteCount(d);
        int rendered = 0;

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Config, filter, d.ProfileName, (w, ms) =>
        {
            int Verdicts(SkseRefVerdict v) => flatAll.Count(r => r.Verdict == v);
            w.WriteStartObject("totals");
            w.WriteNumber("configs_scanned", d.ConfigCount);
            w.WriteNumber("files_with_references", d.Files.Count(x => x.Refs.Count > 0));
            w.WriteNumber("references_checked", flatAll.Count);
            w.WriteNumber("ok", Verdicts(SkseRefVerdict.Ok));
            w.WriteNumber("dangling", Verdicts(SkseRefVerdict.Dangling));
            w.WriteNumber("unparseable", Verdicts(SkseRefVerdict.Unparseable));
            w.WriteNumber("plugin_missing", Verdicts(SkseRefVerdict.PluginMissing));
            // BROKEN is what should resolve and does not; INERT is a reference to a plugin you simply do not have.
            w.WriteNumber("broken", Verdicts(SkseRefVerdict.Dangling) + Verdicts(SkseRefVerdict.Unparseable));
            w.WriteNumber("inert", Verdicts(SkseRefVerdict.PluginMissing));
            w.WriteNumber("read_errors", d.Files.Count(x => x.ReadError is not null));
            w.WriteEndObject();

            w.WriteStartArray("files");
            foreach (var file in files)
            {
                if (SkseJsonDoc.Over(w, ms, cap)) break;
                w.WriteStartObject();
                w.WriteString("rel_path", file.RelPath);
                w.WriteString("file_name", file.FileName);
                w.WriteString("group", file.Group);
                SkseJsonDoc.Nullable(w, "winning_provider", file.WinningProvider);
                w.WriteNumber("provider_count", file.ProviderCount);
                SkseJsonDoc.Providers(w, file.Providers);
                SkseJsonDoc.Nullable(w, "read_error", file.ReadError);
                w.WriteStartArray("references");
                foreach (var r in file.Refs)
                {
                    w.WriteStartObject();
                    w.WriteString("raw", r.Ref.Raw);
                    w.WriteString("shape", r.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate ? "path_segment_gate" : "form_token");
                    w.WriteString("plugin", r.Ref.Plugin);
                    SkseJsonDoc.Nullable(w, "local_id", r.Ref.LocalId is { } id ? $"0x{id:X6}" : null);
                    w.WriteNumber("line", r.Ref.Line);
                    w.WriteString("verdict", VerdictName(r.Verdict));
                    SkseJsonDoc.Nullable(w, "detail", r.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();

            SkseJsonDoc.Caveats(w, d.ReadIncomplete, d.Warnings, d.BsaFailures);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(allFiles.Count, files.Count, rendered, window, notes));
        });
    }

    /// <summary>The verdict's wire spelling — the json twin of <see cref="Tag"/>, from the same enum.</summary>
    static string VerdictName(SkseRefVerdict v) => v switch
    {
        SkseRefVerdict.Ok => "ok",
        SkseRefVerdict.PluginMissing => "plugin_missing",
        SkseRefVerdict.Dangling => "dangling",
        SkseRefVerdict.Unparseable => "unparseable",
        _ => "unknown",
    };

    static void AppendCaveats(StringBuilder sb, SkseConfigAuditData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a config present only in it may be missing from this audit (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}

/// <summary>Renders <see cref="NativePairingAuditData"/>: a health summary, then the diagnostics in full —
/// paired-but-dead (every candidate DLL statically will not load, version-locked mismatches included where the
/// installed runtime is known), locked-but-unverifiable pairings where the runtime is unknown, unpaired classes as a
/// verify flag, debug builds that load on this machine alone, and unreadable-.pex notes — then the accounted-for
/// baseline of engine and skse-core counts and
/// paired-healthy classes grouped by implementing mod. Bounded by max_chars with explicit cut notices. filter= shows a
/// class in full: native function names, pairing evidence, per-DLL manifests and load verdicts, conflict
/// chains.</summary>
static class NativePairingWire
{
    /// <summary>One candidate DLL's static load verdict: LOADS, VERIFY (locked, runtime unknown), or DEAD (a named
    /// static blocker or a locked-runtime mismatch).</summary>
    enum DllFate { Loads, Verify, Dead }

    /// <summary>The verdict a DLL line carries, with the debug-build note appended when it applies. Reaching a LOADS or
    /// VERIFY fate with debug-CRT imports means the debug runtime resolved on THIS machine — the DLL loads for its
    /// author and for nobody else, which the fate alone does not say (#417). A DEAD verdict already names the debug
    /// build in its blocker. The verdict itself is untouched by the note.</summary>
    static (DllFate Fate, string Detail) Judge(NativePairedDll d, string? runtime)
    {
        var (fate, detail) = Verdict(d, runtime);
        if (fate != DllFate.Dead && d.Info is { } info && info.DebugCrtImports.Count > 0)
        {
            var crt = string.Join(", ", info.DebugCrtImports);
            // VERIFY is a version question, so the clause is additive there rather than a 'but': the version lock is
            // still unresolved, AND the debug runtime is a second, independent reason it will not load elsewhere.
            detail += fate == DllFate.Loads
                ? $" — but it imports the debug CRT ({crt}), so it loads HERE and fails with error 126 for anyone " +
                  "without the debug runtime"
                : $" — and it imports the debug CRT ({crt}), so even where the version matches it loads HERE only and " +
                  "fails with error 126 for anyone without the debug runtime";
        }
        return (fate, detail);
    }

    /// <summary>The static load verdict alone — the blocker, the loader's era rules, and the version lock.</summary>
    static (DllFate Fate, string Detail) Verdict(NativePairedDll d, string? runtime)
    {
        if (d.LoadBlocker is { } b) return (DllFate.Dead, b);
        if (d.Info is not { } info) return (DllFate.Verify, "no static manifest read");   // defensive: blocker-less entries carry Info by construction
        if (info.Kind == SksePluginReader.SksePluginKind.LegacyQuery)
        {
            // The AE loader loads only version-data plugins, so a query-only SE/VR-era plugin will not load on a 1.6+
            // runtime — the abandoned SE-era mod on an AE game, which is this tool's headline breakage class.
            if (runtime is { } rt2 && SksePluginReader.IsAeRuntime(rt2))
                return (DllFate.Dead, $"query-only SE/VR-era plugin — the AE loader (installed game is {rt2}) loads only version-data plugins, so it will NOT load");
            if (runtime is not null)
                return (DllFate.Loads, "legacy SE/VR plugin — loads on this SE runtime, but its metadata is set at runtime (not statically verifiable)");
            return (DllFate.Verify, "legacy SE/VR-era query-only plugin — loads on SE (1.5.x) but NOT on an AE (1.6+) runtime; installed game version unknown, verify");
        }
        var v = info.Version!;
        if (v.VersionIndependent)
            return (DllFate.Loads, $"version-independent ({(v.UsesAddressLibrary ? "Address Library" : "signature scanning")})");
        string locked = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
        if (runtime is null)
            return (DllFate.Verify, $"version-LOCKED → {locked} — installed game version unknown, verify it matches");
        return SksePluginReader.RuntimeCompatible(v, runtime)
            ? (DllFate.Loads, $"version-LOCKED → {locked} = installed {runtime}")
            : (DllFate.Dead, $"version-LOCKED → {locked} ≠ installed {runtime} — will NOT load on this game version");
    }

    /// <summary>A paired class's verdict is the best fate among its candidate DLLs: which DLL actually implements the
    /// class is not statically knowable, so one loadable candidate keeps the pairing plausible.</summary>
    static DllFate BestFate(NativeClassEntry c, string? runtime)
    {
        var best = DllFate.Dead;
        foreach (var d in c.PairedDlls)
        {
            var (f, _) = Judge(d, runtime);
            if (f == DllFate.Loads) return DllFate.Loads;
            if (f == DllFate.Verify) best = DllFate.Verify;
        }
        return best;
    }

    /// <summary>True when every candidate DLL that could implement the class is a debug build, and at least one of them
    /// loads here — the class whose implementation exists on the author's machine alone (#417). A class with one clean
    /// candidate is healthy however many debug siblings sit beside it; those siblings still carry the note on their own
    /// line. A clean VERIFY candidate disqualifies exactly as a clean LOADS one does: it is a version question, not a
    /// dead DLL, and it implements the class for everyone on a matching runtime — so "loads here and nowhere else"
    /// would be a claim the data does not support. Only a DEAD candidate is passed over, because it implements the
    /// class nowhere.</summary>
    static bool IsDebugOnly(NativeClassEntry c, string? runtime)
    {
        bool any = false;
        foreach (var dll in c.PairedDlls)
        {
            var fate = Judge(dll, runtime).Fate;
            if (fate == DllFate.Dead) continue;
            if (dll.Info is not { } i || i.DebugCrtImports.Count == 0) return false;
            any |= fate == DllFate.Loads;
        }
        return any;
    }

    /// <summary>What this family's accounting counts: the native-declaring CLASS rows.</summary>
    internal const string RowNoun = "class(es)";

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count.</summary>
    internal static int NoteCount(NativePairingAuditData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count;

    /// <summary>The class population split into the five disjoint buckets the view reports. One function so the
    /// whole-audit summary and the windowed row sections are classified the same way and cannot drift.</summary>
    readonly record struct ClassSplit(List<NativeClassEntry> Engine, List<NativeClassEntry> SkseCore,
                                      List<NativeClassEntry> Unpaired, List<NativeClassEntry> Dead,
                                      List<NativeClassEntry> Verify, List<NativeClassEntry> DebugBuilds,
                                      List<NativeClassEntry> Healthy);

    static ClassSplit Classify(IReadOnlyList<NativeClassEntry> classes, string? runtime)
    {
        var third = classes.Where(c => c.Provenance == NativeProvenance.ThirdParty).ToList();
        // One fate pass per paired class: the section split and the per-DLL tags come from the same Judge, so they
        // cannot disagree.
        var byFate = third.Where(c => c.Rung != NativePairingRung.Unpaired).ToLookup(c => BestFate(c, runtime));
        var loads = byFate[DllFate.Loads].ToList();
        // #417: a class whose only loadable candidate is a DEBUG build loads on THIS machine and nowhere else, so it is
        // a finding in its own right rather than a line of the healthy roster — which prints class names, not DLLs, and
        // would have carried the clean checkmark over exactly the file the author needs to hear about. EVERY loading
        // candidate must be debug-built: one clean DLL that loads keeps the class healthy, because that DLL implements
        // the class for everyone.
        var debugBuilds = loads.Where(c => IsDebugOnly(c, runtime)).ToList();
        return new ClassSplit(
            classes.Where(c => c.Provenance == NativeProvenance.Engine).ToList(),
            classes.Where(c => c.Provenance == NativeProvenance.SkseCore).ToList(),
            third.Where(c => c.Rung == NativePairingRung.Unpaired).ToList(),
            byFate[DllFate.Dead].ToList(),
            byFate[DllFate.Verify].ToList(),
            debugBuilds,
            loads.Except(debugBuilds).ToList());
    }

    public static string Render(NativePairingAuditData d, string? filter, int cap, RowWindow window = default)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window);

        // The summary states the WHOLE audit; limit=/offset= window only the classes the sections LIST. Room for the
        // accounting block is held back out of the cap so it is paid for rather than appended past it.
        int notes = NoteCount(d);
        var rows = window.Apply(d.Classes);
        int reserve = TransportAccounting.Reserve(d.Classes.Count, rows.Count, window, notes, RowNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();

        var all = Classify(d.Classes, d.InstalledRuntime);
        var w = Classify(rows, d.InstalledRuntime);
        var engine = all.Engine; var skseCore = all.SkseCore;
        var unpaired = w.Unpaired; var dead = w.Dead; var verify = w.Verify;
        var debugBuilds = w.DebugBuilds; var healthy = w.Healthy;
        // A baseline class is accounted for by the engine / SKSE-core count line, not by a row of its own.
        foreach (var c in rows)
            if (c.Provenance != NativeProvenance.ThirdParty) tally.Mark(c.ClassName);

        var sb = new StringBuilder();
        sb.Append("native pairing audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.PexScanned).Append(" compiled script(s) scanned, ").Append(d.Classes.Count)
          .Append(" class(es) declare native functions\n");
        if (d.InstalledRuntime is { } rt) sb.Append("installed game runtime: ").Append(rt).Append('\n');
        else sb.Append("installed game runtime: could not be resolved — version-LOCKED findings degrade to 'verify'\n");

        if (all.Dead.Count == 0 && all.Unpaired.Count == 0 && all.Verify.Count == 0 && all.DebugBuilds.Count == 0)
        {
            sb.Append("✓ every third-party native class pairs to a mod whose DLL statically loads — nothing dead, nothing unpaired");
            // The checkmark must not claim a universal the scan did not verify: unreadable .pex files were never
            // examined, and they are where an unpaired class could hide.
            sb.Append(d.Unreadable.Count > 0 ? $" ({d.Unreadable.Count} unreadable .pex NOT examined — see below).\n" : ".\n");
        }
        else
        {
            sb.Append(all.Dead.Count > 0 ? "[!] " : "✓ no dead pairings. ");
            if (all.Dead.Count > 0) sb.Append(all.Dead.Count).Append(" class(es) PAIRED BUT DEAD — scripts installed, nothing that could implement them loads");
            if (all.Verify.Count > 0) sb.Append(all.Dead.Count > 0 ? "   ·   " : "").Append(all.Verify.Count).Append(" pairing(s) need a version check");
            if (all.Unpaired.Count > 0) sb.Append(all.Dead.Count > 0 || all.Verify.Count > 0 ? "   ·   " : "").Append(all.Unpaired.Count).Append(" class(es) UNPAIRED (verify)");
            if (all.DebugBuilds.Count > 0) sb.Append(all.Dead.Count > 0 || all.Verify.Count > 0 || all.Unpaired.Count > 0 ? "   ·   " : "")
                                         .Append(all.DebugBuilds.Count).Append(" class(es) paired only to a DEBUG BUILD");
            sb.Append('\n');
        }

        // ── Diagnostics first, in full. ──
        if (dead.Count > 0)
        {
            sb.Append("\nPAIRED BUT DEAD — the high-confidence finding: every candidate DLL statically will not load, so every native these scripts declare is a silent no-op in game (").Append(dead.Count).Append("):\n");
            AppendCapped(sb, dead, cap, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (verify.Count > 0)
        {
            sb.Append("\npaired, version-LOCKED, runtime unknown — verify the listed runtime matches your game (").Append(verify.Count).Append("):\n");
            AppendCapped(sb, verify, cap, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (unpaired.Count > 0)
        {
            sb.Append("\nUNPAIRED — no mod shipping these scripts (winner or chain) ships any SKSE plugin DLL (").Append(unpaired.Count)
              .Append("). A VERIFY flag, not 'broken': most often a declaration copy of a framework that isn't installed — the calls will silently no-op if anything uses them:\n");
            AppendCapped(sb, unpaired, cap, c =>
                $"  - {c.ClassName} ({c.NativeCount} native fn) ← {c.WinningProvider ?? "(no provider)"} ({c.ProviderKind})", tally, c => c.ClassName);
        }
        if (debugBuilds.Count > 0)
        {
            sb.Append("\nDEBUG BUILD — these load on THIS machine and nowhere else (").Append(debugBuilds.Count)
              .Append("). The debug C runtime ships with Visual Studio and is not redistributable, so the DLL fails with " +
                      "error 126 for anyone without it and every native these scripts declare is a silent no-op there. " +
                      "If you built it, ship a Release build; if you installed it, ask its author for one:\n");
            AppendCapped(sb, debugBuilds, cap, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (d.Unreadable.Count > 0)
        {
            sb.Append("\nunreadable .pex — could not be parsed, NOT counted as native-free (").Append(d.Unreadable.Count).Append("):\n");
            AppendCapped(sb, d.Unreadable, cap, u => $"  - {u.RelPath}: {u.Reason}{(u.WinningProvider is { } p ? $"  [← {p}]" : "")}");
        }

        // ── Accounted-for baseline: everything that is not a finding, so nothing is dropped. ──
        sb.Append("\naccounted for: ").Append(engine.Count).Append(" engine class(es) (carried by an official archive — implemented by the game executable) · ")
          .Append(skseCore.Count).Append(" SKSE-core class(es) (skse64's script additions — implemented by the game-root loader)");
        // Tri-state: false means checked and genuinely absent, so the definite note; null means the check itself
        // failed, which must not render as a checked-and-absent verdict.
        if (skseCore.Count > 0 && d.SkseLoaderSeen == false)
            sb.Append("\n  [!] SKSE-core classes are present but no skse64 loader is visible (game root or enabled mods' Root\\ folders) — if SKSE isn't actually installed, every one of these is dead");
        else if (skseCore.Count > 0 && d.SkseLoaderSeen is null)
            sb.Append("\n  (skse64 loader visibility could not be checked)");
        sb.Append("\npaired healthy (").Append(healthy.Count).Append(" class(es)) — implementing mod ← its classes:\n");
        foreach (var g in healthy.GroupBy(c => c.PairedMod ?? "(?)", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sb.Length >= cap) { sb.Append("  ... [remaining healthy groups omitted; raise max_chars]\n"); break; }
            sb.Append("  - ").Append(g.Key).Append(": ").Append(string.Join(", ", g.Select(c => c.ClassName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))).Append('\n');
            foreach (var c in g) tally.Mark(c.ClassName);
        }

        sb.Append("\n(scope: what the winning compiled scripts DECLARE, statically paired to what their mods ship. 'Paired' means the " +
                  "co-shipment evidence is plausible and a candidate DLL loads — NEVER that the DLL registers exactly these functions " +
                  "(registration is runtime behavior, the honest ceiling). Which mods CALL an unpaired class is not scanned (a possible Wave 2).)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<class/mod/DLL>' for full detail: native function names, pairing evidence, per-DLL manifests and load verdicts.");
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Classes.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>The one-block render of a dead/verify pairing: the class line, then each candidate DLL's fate.</summary>
    static string DeadLine(NativeClassEntry c, string? runtime)
    {
        var sb = new StringBuilder();
        sb.Append("  - ").Append(c.ClassName).Append(" (").Append(c.NativeCount).Append(" native fn) ← ")
          .Append(c.WinningProvider ?? "(no provider)")
          .Append(c.Rung == NativePairingRung.ChainMod ? $" — paired via the conflict chain to {c.PairedMod}" : $" — paired to {c.PairedMod}");
        foreach (var dll in c.PairedDlls)
            sb.Append("\n      ").Append(DllLine(dll, runtime, withVersion: false));
        return sb.ToString();
    }

    /// <summary>The per-DLL fate line, shared by the default and filter= views so the two cannot drift:
    /// "[FATE] group\file ("name" vX)? — detail".</summary>
    static string DllLine(NativePairedDll dll, string? runtime, bool withVersion)
    {
        var (fate, detail) = Judge(dll, runtime);
        var sb = new StringBuilder();
        sb.Append(fate switch { DllFate.Dead => "[DEAD] ", DllFate.Verify => "[VERIFY] ", _ => "[LOADS] " })
          .Append(dll.Group.Length > 0 ? dll.Group + "\\" : "").Append(dll.FileName);
        if (withVersion && dll.Info?.Version is { } v) sb.Append("  \"").Append(v.Name).Append("\" v").Append(v.PluginVersion);
        sb.Append(" — ").Append(detail);
        return sb.ToString();
    }

    /// <summary>filter=: full detail for every matching class — by class name, path, provider, paired mod, or a
    /// candidate DLL's filename — giving the declared native functions, the pairing evidence, each candidate DLL's
    /// manifest and load verdict, and the conflict chain.</summary>
    static string RenderFiltered(NativePairingAuditData d, string filter, int cap, RowWindow window = default)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(NativeClassEntry c) => In(c.ClassName) || In(c.RelPath) || In(c.WinningProvider) || In(c.PairedMod)
            || c.PairedDlls.Any(x => In(x.FileName)) || c.Providers.Any(p => In(p.Name));
        var allHits = d.Classes.Where(Match).OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase).ToList();

        int notes = NoteCount(d);
        var hits = window.Apply(allHits);
        int reserve = TransportAccounting.Reserve(allHits.Count, hits.Count, window, notes, RowNoun);
        cap = Math.Max(1, cap - reserve);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(allHits.Count, hits.Count, tally.Count, window, notes), RowNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("native pairing audit — filter '").Append(filter).Append("' — ")
          .Append(allHits.Count).Append(" class(es) match [profile '").Append(d.ProfileName).Append("']\n");
        if (allHits.Count == 0)
        {
            // The suggestion pool spans every axis Match filters on: class names, providers, paired mods, and DLL
            // filenames. PluginNameSuggest dedups and skips empties.
            var pool = d.Classes.Select(c => c.ClassName)
                .Concat(d.Classes.Select(c => c.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.Select(c => c.PairedMod).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.SelectMany(c => c.PairedDlls.Select(x => x.FileName)));
            sb.Append("\nno native-declaring class matched. ").Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, pool));
            sb.Append('\n');
            AppendCaveats(sb, d);   // a "no match" over an incompletely-read build must carry the caveat (Q3)
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        int shown = 0;
        foreach (var c in hits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [showing ").Append(shown).Append(" of ").Append(hits.Count).Append(" classes; raise max_chars]\n"); break; }
            sb.Append('\n').Append(c.ClassName).Append("  (").Append(c.RelPath).Append(")\n");
            sb.Append("  provenance: ").Append(c.Provenance switch
            {
                NativeProvenance.Engine => "ENGINE — carried by an official archive; implemented by the game executable (baseline)",
                NativeProvenance.SkseCore => "SKSE CORE — skse64's script additions; implemented by the game-root loader (baseline)",
                _ => c.Rung switch
                {
                    NativePairingRung.SameMod => $"third-party, paired to its own provider ({c.PairedMod})",
                    NativePairingRung.ChainMod => $"third-party, paired via the conflict chain to {c.PairedMod}",
                    _ => "third-party, UNPAIRED — no mod in this file's chain ships any SKSE plugin DLL (verify)",
                },
            }).Append('\n');
            if (c.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(c.ProviderCount).Append(" sources (winner scanned): ")
                  .Append(string.Join(" › ", c.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            else
                sb.Append("  provider: ").Append(c.WinningProvider ?? "(none)").Append(" (").Append(c.ProviderKind).Append(")\n");
            foreach (var dll in c.PairedDlls)
                sb.Append("  ").Append(DllLine(dll, d.InstalledRuntime, withVersion: true)).Append('\n');
            sb.Append("  native functions (").Append(c.NativeCount).Append("): ");
            var fns = string.Join(", ", c.NativeFunctions);
            if (sb.Length + fns.Length > cap && c.NativeCount > 8)
                sb.Append(string.Join(", ", c.NativeFunctions.Take(8))).Append(", ... [").Append(c.NativeCount - 8).Append(" more; raise max_chars]");
            else sb.Append(fns);
            sb.Append('\n');
            shown++; tally.Mark(c.ClassName);
        }
        // The caveats ride the filtered view too: "no match", or a partial hit over a build whose BSA failed to read,
        // must not read as a clean answer.
        sb.Append('\n');
        AppendCaveats(sb, d);
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    static void AppendCapped<T>(StringBuilder sb, IReadOnlyList<T> items, int cap, Func<T, string> line,
                                RowTally? tally = null, Func<T, string>? key = null)
    {
        int shown = 0;
        foreach (var e in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter= to see all]\n"); break; }
            sb.Append(line(e)).Append('\n'); shown++;
            if (tally is not null && key is not null) tally.Mark(key(e));
        }
    }

    /// <summary>The json twin of <see cref="Render"/>: the same census, the same windowed classes, and the same
    /// per-DLL fates — from the same <see cref="Judge"/>, so the two renders cannot disagree about what loads.</summary>
    public static string RenderJson(NativePairingAuditData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allClasses = filtered
            ? d.Classes.Where(c => In(c.ClassName) || In(c.RelPath) || In(c.WinningProvider) || In(c.PairedMod)
                                || c.PairedDlls.Any(x => In(x.FileName)) || c.Providers.Any(pr => In(pr.Name)))
                       .OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase).ToList()
            : d.Classes.ToList();
        var classes = window.Apply(allClasses);
        var all = Classify(d.Classes, d.InstalledRuntime);
        int notes = NoteCount(d);
        int rendered = 0;

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Pairing, filter, d.ProfileName, (w, ms) =>
        {
            SkseJsonDoc.Nullable(w, "installed_runtime", d.InstalledRuntime);
            // Tri-state: null is "the check itself failed", never a checked-and-absent verdict.
            if (d.SkseLoaderSeen is { } seen) w.WriteBoolean("skse_loader_seen", seen); else w.WriteNull("skse_loader_seen");
            w.WriteStartObject("totals");
            w.WriteNumber("classes", d.Classes.Count);
            w.WriteNumber("pex_scanned", d.PexScanned);
            w.WriteNumber("engine", all.Engine.Count);
            w.WriteNumber("skse_core", all.SkseCore.Count);
            w.WriteNumber("unpaired", all.Unpaired.Count);
            w.WriteNumber("dead", all.Dead.Count);
            w.WriteNumber("verify", all.Verify.Count);
            w.WriteNumber("debug_build", all.DebugBuilds.Count);
            w.WriteNumber("healthy", all.Healthy.Count);
            w.WriteNumber("unreadable_pex", d.Unreadable.Count);
            w.WriteEndObject();

            w.WriteStartArray("classes");
            foreach (var c in classes)
            {
                if (SkseJsonDoc.Over(w, ms, cap)) break;
                w.WriteStartObject();
                w.WriteString("class_name", c.ClassName);
                w.WriteString("rel_path", c.RelPath);
                w.WriteString("provenance", c.Provenance switch
                {
                    NativeProvenance.Engine => "engine",
                    NativeProvenance.SkseCore => "skse_core",
                    _ => "third_party",
                });
                SkseJsonDoc.Nullable(w, "rung", c.Rung switch
                {
                    NativePairingRung.SameMod => "same_mod",
                    NativePairingRung.ChainMod => "chain_mod",
                    NativePairingRung.Unpaired => "unpaired",
                    _ => null,
                });
                w.WriteString("verdict", VerdictName(c, d.InstalledRuntime));
                SkseJsonDoc.Nullable(w, "winning_provider", c.WinningProvider);
                w.WriteString("provider_kind", c.ProviderKind);
                w.WriteNumber("provider_count", c.ProviderCount);
                SkseJsonDoc.Providers(w, c.Providers);
                SkseJsonDoc.Nullable(w, "paired_mod", c.PairedMod);
                w.WriteNumber("native_count", c.NativeCount);
                SkseJsonDoc.Strings(w, "native_functions", c.NativeFunctions);
                w.WriteStartArray("paired_dlls");
                foreach (var dll in c.PairedDlls)
                {
                    var (fate, detail) = Judge(dll, d.InstalledRuntime);
                    w.WriteStartObject();
                    w.WriteString("rel_path", dll.RelPath);
                    w.WriteString("file_name", dll.FileName);
                    w.WriteString("group", dll.Group);
                    SkseJsonDoc.Nullable(w, "winning_provider", dll.WinningProvider);
                    w.WriteString("fate", fate.ToString().ToLowerInvariant());
                    w.WriteString("detail", detail);
                    SkseJsonDoc.Nullable(w, "plugin_name", dll.Info?.Version?.Name);
                    SkseJsonDoc.Nullable(w, "plugin_version", dll.Info?.Version?.PluginVersion);
                    SkseJsonDoc.Strings(w, "debug_crt_imports", dll.Info?.DebugCrtImports ?? Array.Empty<string>());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();

            w.WriteStartArray("unreadable_pex");
            foreach (var u in d.Unreadable)
            {
                if (SkseJsonDoc.Over(w, ms, cap)) break;
                w.WriteStartObject();
                w.WriteString("rel_path", u.RelPath);
                SkseJsonDoc.Nullable(w, "winning_provider", u.WinningProvider);
                w.WriteString("reason", u.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            SkseJsonDoc.Caveats(w, d.ReadIncomplete, d.Warnings, d.BsaFailures);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(allClasses.Count, classes.Count, rendered, window, notes));
        });
    }

    /// <summary>Which section of the text render this class lands in, as one word — from the same Judge, so the two
    /// renders classify identically.</summary>
    static string VerdictName(NativeClassEntry c, string? runtime)
    {
        if (c.Provenance != NativeProvenance.ThirdParty) return "baseline";
        if (c.Rung == NativePairingRung.Unpaired) return "unpaired";
        return BestFate(c, runtime) switch
        {
            DllFate.Dead => "dead",
            DllFate.Verify => "verify",
            _ => IsDebugOnly(c, runtime) ? "debug_build" : "healthy",
        };
    }

    static void AppendCaveats(StringBuilder sb, NativePairingAuditData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a script present only in it may be missing from this audit (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}

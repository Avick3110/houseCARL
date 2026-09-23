using System.ComponentModel;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only view of the SKSE layer of the active load order: the .dll plugins under Data\SKSE\Plugins, the
/// configs beneath them, and the native Papyrus functions the order's compiled scripts declare. ONE finding family
/// per call, each render in its own wire class and file; contract in docs/architecture/skse-layer.md.</summary>
[McpServerToolType]
public static class SkseTools
{
    /// <summary>The three finding families <c>findings=</c> selects between.</summary>
    internal enum SkseFamily { Inventory, Pairing, Config }

    /// <summary>Parses <c>findings=</c> off the wire, where the schema declares it string-or-array: the array shape must
    /// BIND so this tool's own one-family refusal answers it rather than the shim's type-mismatch sentence, and every
    /// non-string shape is refused by its raw JSON — a one-element array too, never unwrapped to the scalar. Pinned by
    /// SkseFindingsWireShapeTests.</summary>
    internal static bool TryParseFamily(System.Text.Json.JsonElement? findings, out SkseFamily family, out string? error)
        => TryParseFamily(findings switch
        {
            null or { ValueKind: System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined } => null,
            { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
            { } el => el.GetRawText(),
        }, out family, out error);

    /// <summary>Parses <c>findings=</c>; omitted is the inventory family, and an unknown value is refused, never defaulted.</summary>
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
        // Naming the SHAPE is what turns the refusal into a fix; "not a family" reads as the wrong word.
        var shape = token.Contains(',') || token.Contains('[')
            ? " findings= here takes ONE value, not a list."
            : "";
        error = $"error: findings='{token}' is not a family on this tool — pass findings='inventory' (the DLL and config " +
                "layer), 'pairing' (native Papyrus declarations vs the DLLs that implement them) or 'config' (the form " +
                $"references SKSE configs declare vs your load order).{shape} One family per call.";
        return false;
    }

    /// <summary>The <c>peek=</c> family check, or null; peek= reads a DLL image, so the other two families refuse it.</summary>
    internal static string? PeekFamilyError(bool peek, SkseFamily family) =>
        peek && family != SkseFamily.Inventory
            ? $"error: peek= is the inventory family's — findings='{family.ToString().ToLowerInvariant()}' never reads a " +
              "DLL image. Drop peek=, or pass findings='inventory' with filter='<DLL/plugin/mod name>'."
            : null;

    /// <summary>The line every response ends on: which family ran, and the exact spelling for the two that did not.</summary>
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

    /// <summary>The three family renders, one method each — a seam a test can drive with no live MO2 instance.</summary>
    internal interface IFamilyRenders
    {
        string Inventory(FamilyCall c);
        string Pairing(FamilyCall c);
        string Config(FamilyCall c);
    }

    /// <summary>One call's shared render context, a record so a new TRANSPORT axis lands here rather than as a fourth
    /// argument. <c>Trailer</c> is held out of the render's BUDGET while <c>Cap</c> stays the caller's own max_chars.</summary>
    internal readonly record struct FamilyCall(string? Filter, bool Peek, int Cap, RowWindow Window, bool Json, int Trailer = 0);

    /// <summary>The live renders: each family's data read from the service, handed to its own wire class.</summary>
    sealed class ServiceRenders(LoadOrderService svc) : IFamilyRenders
    {
        public string Inventory(FamilyCall c)
        {
            var d = svc.SkseInventory(c.Peek ? c.Filter!.Trim() : null);
            return c.Json ? SkseInventoryWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : SkseInventoryWire.Render(d, c.Filter, c.Cap, c.Window, c.Trailer);
        }

        public string Pairing(FamilyCall c)
        {
            var d = svc.NativePairingAudit();
            return c.Json ? NativePairingWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : NativePairingWire.Render(d, c.Filter, c.Cap, c.Window, c.Trailer);
        }

        public string Config(FamilyCall c)
        {
            var d = svc.SkseConfigAudit();
            return c.Json ? SkseConfigAuditWire.RenderJson(d, c.Filter, c.Cap, c.Window)
                          : SkseConfigAuditWire.Render(d, c.Filter, c.Cap, c.Window, c.Trailer);
        }
    }

    /// <summary>Runs the selected family and appends the footer, which rides down as the render's TRAILER so every notice
    /// quotes the max_chars the caller passed; the charging rule is in docs/architecture/skse-layer.md.</summary>
    internal static string Dispatch(IFamilyRenders renders, SkseFamily family, string? filter, bool peek, int max_chars,
                                    bool json = false, RowWindow window = default)
    {
        // The json document states the family and the two that did not run in-band, so no text footer.
        var footer = json ? "" : FamilyFooter(family);
        int cap = max_chars > 0 ? max_chars : 80_000;
        var call = new FamilyCall(filter, peek, cap, window, json, footer.Length);
        var body = family switch
        {
            SkseFamily.Inventory => renders.Inventory(call),
            SkseFamily.Pairing => renders.Pairing(call),
            _ => renders.Config(call),
        };
        // The one arm a bounded render may still exceed on is NAMED rather than left to be discovered. The json
        // documents name it INSIDE themselves (max_chars_overrun), so the text notice must not be glued on past
        // their root close, which would stop them being json at all.
        return json ? body : RenderCap.Settle(body + footer, cap);
    }

    /// <summary>The two families this call did not run, in the spelling that would — the json twin of <see cref="FamilyFooter"/>.</summary>
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
            "WITHOUT loading or running it. That version is the AUTHOR'S OWN DECLARATION and is routinely stale or " +
            "coarse (SPID 7.3.3 declares 7.0.0), so every version is labelled with the source it came from, and the " +
            "DLL's build-stamped file version and the mod's MO2 meta.ini version are printed on the same line wherever " +
            "they disagree with it. Leads with the diagnostics: version-LOCKED plugins (won't load on a " +
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
            // JsonElement, not string: the array shape must BIND so the refusal above answers it.
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
        [Description("TRANSPORT: character CEILING on the whole response — the row that would cross it is not written, and every list says what it held back. The scope note, the caveats, the filter hint and the family footer are charged before the rows render, so all four are inside the ceiling. A cap too small for what the family carries whatever the budget says so and names the cap that clears it. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.Skse, () =>
    {
        // The argument checks run BEFORE the config prompt, and format= first of all, because every refusal
        // below has to be answered in the shape the caller asked for.
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

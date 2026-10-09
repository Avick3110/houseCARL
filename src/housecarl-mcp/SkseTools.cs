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
        int cap = Wire.Cap(max_chars);
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
         "The SKSE layer of the active load order, which the record and asset tools do not read: the .dll plugins " +
         "under Data\\SKSE\\Plugins, every .ini/.toml/.json/.yaml/.yml config beneath them, and the native Papyrus " +
         "functions the order's compiled scripts declare. Read-only. One family per call, chosen by findings= " +
         "('inventory' when omitted, 'pairing' or 'config'); every response names the family it ran and how to run " +
         "the other two. Everything here is what a file declares, never what the DLL does: loading, registering, " +
         "hooking and reading happen at runtime and are never observed, and a missing token proves nothing. Treat a " +
         "finding as a plausibility verdict to verify, and 'nothing found' as no clean bill of health. Not covered: " +
         "distributor INIs in the Data root (SPID *_DISTR, KID *_KID), which the spid-authoring and kid-authoring " +
         "skills own. Every response ends on its accounting (total / rendered / skipped / capped / truncated / " +
         "offset / remaining / notes), so what a window or a cap left out is a number.")]
    public static string Skse(
        LoadOrderService svc,
        [Description(
            "Optional. Which family to run: one value, not a list; 'inventory' when omitted. " +
            "'inventory': every .dll and config at any depth under Data\\SKSE\\Plugins, each with the mod that wins " +
            "it. Configs are grouped by their real subfolder (SkyPatcher, DynamicStringDistributor, OStim, ...); " +
            "non-config content is counted. For each modern plugin it reads the static manifest the SKSE loader " +
            "reads (name, author, version, Address Library or locked to specific game runtimes, the XSE floor) from " +
            "the DLL's SKSEPlugin_Version export, without loading it. That version is the author's own declaration " +
            "and often stale, so each version names its source, and the DLL's file version and the mod's MO2 " +
            "meta.ini version are printed beside it where they disagree. Leads with version-locked plugins (won't " +
            "load on a mismatched game version), legacy query-only plugins (metadata set at runtime, not readable " +
            "here), non-plugin DLLs (bundled dependencies), subfolder DLLs (not on SKSE's loader path), DLLs " +
            "contested by more than one mod, and debug-build plugins (they import the debug C runtime and fail with " +
            "error 126 without Visual Studio installed). " +
            "'pairing': native Papyrus declarations against the DLLs that implement them, for scripts installed " +
            "while their DLL is missing, won't load on this game version, or is 32-bit, BSA-packed or in a " +
            "subfolder. It scans the winning copy of every compiled script (loose and BSA). A class carried by an " +
            "official archive counts as the engine's, even when SKSE's loose override wins the file, and skse64's " +
            "own script additions count as SKSE core. Each remaining class is paired to the DLLs its provider mod, " +
            "or a mod in its conflict chain, ships under SKSE\\Plugins. Leads with paired-but-dead (every candidate " +
            "DLL statically will not load: wrong game runtime for a version-locked plugin, BSA-only, subfolder, " +
            "32-bit, unreadable, debug-built) and unpaired (no DLL in sight: a flag to verify, typically a " +
            "declaration copy of a framework you don't have, never called broken). It answers whether a pairing is " +
            "plausible and healthy, never whether the DLL registers exactly these functions. " +
            "'config': whether the form references in SKSE configs resolve. It reads the winning copy of every " +
            ".ini/.toml/.json/.yaml/.yml at any depth under Data\\SKSE\\Plugins and extracts every form-shaped " +
            "reference: a hex FormID beside a plugin filename in either order (0xFORM|Plugin.esp as DSD, CDF and " +
            "po3 write it, Plugin.esp|0xFORM as SkyPatcher writes it, the ~ tilde form), IED's JSON {\"id\",\"plugin\"} " +
            "objects and plugin-named folder gates (DynamicStringDistributor\\Plugin.esp\\...). Each resolves against the active order as OK, " +
            "PLUGIN MISSING (plugin not in the order), DANGLING (plugin present, no such record) or UNPARSEABLE " +
            "(shape-matched but cannot be normalized), summed as BROKEN (dangling or unparseable, actionable) versus " +
            "INERT (plugin missing, usually optional support for a mod you aren't running). It checks that a " +
            "reference resolves, never what it is for. Extraction matches token shapes, so a token in a comment or " +
            "a disabled block still surfaces; 'no references found' is the commonest per-file result, not a " +
            "warning. Bare EditorID and name strings are not validated.")]
            // JsonElement, not string: the array shape must BIND so the refusal above answers it.
            System.Text.Json.JsonElement? findings = null,
        [Description(
            "Optional. A case-insensitive substring narrowing whichever family ran, matched in that family's own " +
            "domain. inventory: a plugin name, author, DLL filename, providing mod, or config folder ('SkyPatcher', " +
            "'EngineFixes', 'po3', 'OStim'); it expands that folder to its files, or shows one plugin in full (all " +
            "flags, compatible runtimes, email, providers, configs). pairing: a script class name, providing mod, " +
            "paired mod, or DLL filename; full detail per matching class: the declared native functions, the " +
            "pairing evidence, each candidate DLL's manifest and load verdict, the conflict chains. config: a " +
            "config folder, providing mod, filename, or referenced plugin name; it audits just those configs and " +
            "lists every reference with its verdict, OKs included, which confirms a patch you just wrote. Omit for " +
            "the family's whole-layer view.")]
            string? filter = null,
        [Description(
            "Optional. Inventory family only. Statically reads the image of each DLL filter= matches: the DLLs it " +
            "imports (with derived flags: graphics/input hooks, network, and which bundled non-plugin DLL it uses), " +
            "the config paths it embeds (which folder it actually scans), and the plugin names it embeds, each " +
            "checked against your load order. Answers 'what does this unfamiliar DLL touch'.")]
            bool peek = false,
        [Description("Optional. 'text' (default) | 'json' (the same rows and accounting as the family that ran, in named fields).")]
            string? format = null,
        [Description("Optional. Max rows to render from the family's row list: inventory the DLLs (with filter=, the DLL and config matches), pairing the native-declaring classes, config the config files. 0 = no limit. The census above the rows always covers the whole layer.")]
            int limit = 0,
        [Description("Optional. Skip the first N rows of the family's row list, for paging. 0 = the beginning.")]
            int offset = 0,
        [Description("Optional. Character ceiling on the whole response; the row that would cross it is not written, and every list says what it held back. The scope note, the caveats, the filter hint and the family footer are always inside the ceiling; a cap too small for them says so and names the cap that clears it. 0 = the server default (~40k).")]
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

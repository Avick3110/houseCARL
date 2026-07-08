using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL NIF tool. Read-only. Reads the DATA VALUES inside a Skyrim mesh (.nif) — header/version, the block census,
/// each shape's name + NiAVObject flags + scale, its BSDismember partitions, its alpha property, its texture-set paths
/// and bone list, the node tree, and the header string table — resolving the Data-relative path through MO2's VFS to the
/// WINNING copy first (loose beats BSA), the same file-layer precedence the asset tools use. Rides NiflySharp
/// (source-generated from nifxml = coverage by construction); reads BSA-packed meshes straight from archive bytes with
/// no disk extraction, and holds no file handles at rest. This is the asset-INTERNAL counterpart to housecarl_asset_status
/// (which answers WHICH file wins): once you know the winning mesh, this answers WHAT IS INSIDE it. Data values in; geometry
/// / visual content stays out of this capability by design (PRFAQ NIF-layer scope).
/// </summary>
[McpServerToolType]
public static class NifTools
{
    static readonly string[] KnownSections = { "shapes", "partitions", "alpha", "paths", "strings", "nodes", "bones" };

    [McpServerTool(Name = "housecarl_nif_inspect", ReadOnly = true, Title = "Inspect the data values inside a Skyrim mesh (.nif)"),
     Description(
         "Read the DATA VALUES inside a Skyrim mesh (.nif) at the data layer, beneath NifSkope. Resolve the Data-relative " +
         "mesh_path through Mod Organizer 2's virtual file system to the copy the game actually uses (loose beats BSA; among " +
         "BSAs the later-loaded plugin wins) and report, from that mesh: its header version + whether it is a Skyrim SE " +
         "stream; the block census (every block type and count); any UNKNOWN blocks (named + preserved, never silently " +
         "dropped); and per shape — the shape name, the NiAVObject flags (hex, decoded by deviation from the type's " +
         "documented default plus the 0x80000 bit) and scale, the BSDismember body-part " +
         "partitions (decoded to their SBP_* names), the alpha property (decoded blend / test / threshold), the embedded " +
         "texture-set paths, and the bone list; plus the node tree and the header string table. Use it to answer 'what " +
         "shapes / bones / textures / partitions / alpha does this mesh have', to read a facegen mesh's baked shape names " +
         "and tint path, to check a skeleton's bone names, or to see a dark-face mesh's flags/alpha/partitions — the " +
         "asset-INTERNAL companion to housecarl_asset_status (which mod wins) once you know the winning file. Output is a " +
         "SUMMARY by default (header + census + shape names); pass sections to expand ('shapes','partitions','alpha'," +
         "'paths','strings','nodes','bones', or 'all'). Pass mod= to inspect a specific provider instead of the winner. An " +
         "unreadable archive, an absent path, or a mesh NiflySharp refuses (e.g. its strict non-0/1 boolean class) is " +
         "reported LOUD by name — never a silent 'absent' or a half-answer. Read-only: resolves nothing to disk, writes " +
         "nothing, changes no load order. Scope: data values only; it does not read or edit geometry / visual content.")]
    public static string NifInspect(
        LoadOrderService svc,
        [Description("The Data-relative mesh path to inspect, e.g. " +
                     "'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif' or " +
                     "'meshes\\armor\\iron\\cuirass_1.nif'. Relative to the game's Data folder (forward or back slashes both fine).")]
            string mesh_path,
        [Description("Optional. Which detail sections to show beyond the summary — any of 'shapes', 'partitions', 'alpha', " +
                     "'paths', 'strings', 'nodes', 'bones', or 'all'. Comma- or space-separated. Empty = summary only " +
                     "(header + block census + shape names).")]
            string sections = "",
        [Description("Optional. Inspect a specific provider's copy of the mesh instead of the VFS winner — the mod folder " +
                     "name, 'overwrite', 'Data', or a BSA filename as listed in the providers chain. Empty = the winner.")]
            string mod = "",
        [Description("Optional. Max characters before a detail list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_nif_inspect", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(mesh_path))
            return "error: mesh_path is empty. Pass a Data-relative mesh path (e.g. 'meshes\\armor\\iron\\cuirass_1.nif').";

        var (want, unknownTokens) = ParseSections(sections);
        var data = svc.NifInspect(mesh_path, string.IsNullOrWhiteSpace(mod) ? null : mod);
        return NifWire.Render(data, want, unknownTokens, max_chars > 0 ? max_chars : 80_000);
    });

    /// <summary>Parse the sections argument into the recognized set + a list of any UNRECOGNIZED tokens (surfaced, never
    /// silently ignored — Q3). 'all' expands to every known section.</summary>
    static (HashSet<string> Want, IReadOnlyList<string> Unknown) ParseSections(string sections)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var raw in (sections ?? "").Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) { foreach (var s in KnownSections) want.Add(s); }
            else if (Array.Exists(KnownSections, s => s.Equals(raw, StringComparison.OrdinalIgnoreCase))) want.Add(raw.ToLowerInvariant());
            else unknown.Add(raw);
        }
        return (want, unknown);
    }
}

/// <summary>Renders <see cref="NifInspectData"/> as compact, scannable text: the build-level Q3 alarms first (archives
/// that failed to read; discovery warnings), then the resolution (which copy was read + the provider chain), then — on a
/// clean read — the summary (version, block census, unknown-block report, shape names, node count) and any requested
/// detail sections. An ABSENT / bad-path / unreadable / parse-refused result is the error line, loud and named. Detail
/// lists are bounded by max_chars with an explicit cut notice (Q3 — never silent truncation).</summary>
static class NifWire
{
    public static string Render(NifInspectData d, HashSet<string> want, IReadOnlyList<string> unknownSections, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("nif inspect — ").Append(d.RelPath)
          .Append("  (profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)").Append("')\n");

        // Q3 alarms FIRST, so a long detail dump can't truncate them away.
        AppendReadFailures(sb, d.BsaFailures, cap);
        AppendDiscoveryWarnings(sb, d.Warnings, cap);
        if (unknownSections.Count > 0)
            sb.Append("\n[!] unrecognized section(s) ignored: ").Append(string.Join(", ", unknownSections))
              .Append("  (known: ").Append(string.Join(", ", new[] { "shapes", "partitions", "alpha", "paths", "strings", "nodes", "bones", "all" })).Append(")\n");

        // Error path (ABSENT / bad path / unreadable / parse-refused). Still show the provider chain when we have one.
        if (d.Inspect is null)
        {
            sb.Append('\n').Append(d.Error ?? "unknown error").Append('\n');
            if (d.Providers.Count > 0) AppendProviders(sb, d.Providers);
            return sb.ToString().TrimEnd('\n');
        }

        var nif = d.Inspect;
        sb.Append("\n  read from: ").Append(d.Inspected!.Name).Append(" (").Append(d.Inspected.Kind).Append(")\n");
        AppendProviders(sb, d.Providers);
        if (d.Ambiguous)
            sb.Append("  note: more than one source provides this mesh — the winner above was read (loose beats BSA). " +
                      "Pass mod= to inspect another provider's copy.\n");

        sb.Append("  version: ").Append(nif.VersionString.Length > 0 ? nif.VersionString : "(unknown)")
          .Append("  user ").Append(nif.UserVersion).Append(" stream ").Append(nif.StreamVersion)
          .Append(nif.IsSkyrimSE ? "  [Skyrim SE]" : "  [NOT an SE stream — LE / FO4 / other]").Append('\n');

        AppendClampedList(sb, "  blocks: " + nif.BlockCount + " — ", nif.BlockTypes.Select(t => t.Type + " x" + t.Count), cap);

        if (!nif.HasUnknownBlocks)
            sb.Append("  unknown blocks: none\n");
        else if (nif.UnknownBlockTypes.Count == 0)
            // The library flagged unknown blocks but none resolved to a NiUnknown we could name (theoretical) — say so
            // honestly rather than render a bare "0 type(s) —".
            sb.Append("  unknown blocks: present (types not named — preserved intact, reported not modeled)\n");
        else
            sb.Append("  unknown blocks: ").Append(nif.UnknownBlockTypes.Count).Append(" type(s) — ")
              .Append(string.Join(", ", nif.UnknownBlockTypes))
              .Append("  (preserved intact, reported not modeled — likely another game's format)\n");

        AppendClampedList(sb, "  shapes (" + nif.Shapes.Count + "): ", nif.Shapes.Select(s => "'" + s.Name + "'"), cap);
        sb.Append("  nodes: ").Append(nif.Nodes.Count).Append("  (pass sections=nodes for the tree)\n");

        // ---- detail sections on demand ----
        if (want.Contains("shapes")) RenderShapesDetail(sb, nif, cap);
        if (want.Contains("partitions")) RenderPerShape(sb, nif, cap, "partitions", s => s.Partitions.Count > 0,
            s => string.Join(", ", s.Partitions.Select(p => $"{p.BodyPartId} ({p.BodyPartName}, flags {p.PartFlags})")));
        if (want.Contains("alpha")) RenderPerShape(sb, nif, cap, "alpha", s => s.Alpha is not null,
            s => AlphaLine(s.Alpha!));
        if (want.Contains("paths")) RenderPaths(sb, nif, cap);
        if (want.Contains("bones")) RenderPerShape(sb, nif, cap, "bones", s => s.Bones.Count > 0,
            s => string.Join(", ", s.Bones));
        if (want.Contains("nodes")) RenderNodes(sb, nif, cap);
        if (want.Contains("strings")) RenderStrings(sb, nif, cap);

        return sb.ToString().TrimEnd('\n');
    }

    static void AppendProviders(StringBuilder sb, IReadOnlyList<NifProvider> providers)
    {
        sb.Append("  providers (").Append(providers.Count).Append("): ");
        for (int i = 0; i < providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(providers[i].Name).Append(" (").Append(providers[i].Kind).Append(')');
        }
        sb.Append('\n');
    }

    static void RenderShapesDetail(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- shapes (").Append(nif.Shapes.Count).Append(") ---\n");
        foreach (var s in nif.Shapes)
        {
            if (Cut(sb, cap, nif.Shapes.Count)) return;
            sb.Append("  '").Append(s.Name).Append("'  flags ").Append(DescribeFlags(s.Flags, s.FlagsDefault, s.FlagsDefaultType, s.BlockType))
              .Append("  scale ").Append(Fmt(s.Scale)).Append('\n');
            if (s.Partitions.Count > 0)
                sb.Append("    partitions: ").Append(string.Join(", ", s.Partitions.Select(p => $"{p.BodyPartId} ({p.BodyPartName}, flags {p.PartFlags})"))).Append('\n');
            if (s.Alpha is not null)
                sb.Append("    alpha: ").Append(AlphaLine(s.Alpha)).Append('\n');
            foreach (var t in s.Textures)
                sb.Append("    tex[").Append(t.Slot).Append("]: ").Append(t.Path).Append('\n');
            if (s.Bones.Count > 0)
                sb.Append("    bones: ").Append(string.Join(", ", s.Bones)).Append('\n');
        }
    }

    static void RenderPerShape(StringBuilder sb, NifInspect nif, int cap, string title, Func<NifShape, bool> has, Func<NifShape, string> line)
    {
        sb.Append("\n--- ").Append(title).Append(" ---\n");
        var matched = nif.Shapes.Where(has).ToList();   // count the omitted remainder over the FILTERED subset, not the total shapes
        int shown = 0;
        foreach (var s in matched)
        {
            if (Cut(sb, cap, matched.Count - shown)) return;
            sb.Append("  '").Append(s.Name).Append("': ").Append(line(s)).Append('\n');
            shown++;
        }
        if (shown == 0) sb.Append("  (none)\n");
    }

    static void RenderPaths(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- paths (embedded texture-set slots; material/.tri/physics-xml refs appear under sections=strings) ---\n");
        var textured = nif.Shapes.Where(s => s.Textures.Count > 0).ToList();   // omitted remainder counts the FILTERED subset, not total shapes
        int shown = 0;
        foreach (var s in textured)
        {
            if (Cut(sb, cap, textured.Count - shown)) return;
            sb.Append("  '").Append(s.Name).Append("':\n");
            foreach (var t in s.Textures) sb.Append("    tex[").Append(t.Slot).Append("]: ").Append(t.Path).Append('\n');
            shown++;
        }
        if (shown == 0) sb.Append("  (no embedded texture paths)\n");
    }

    static void RenderNodes(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- node tree (").Append(nif.Nodes.Count).Append(") ---\n");
        int shown = 0;
        foreach (var n in nif.Nodes)
        {
            if (Cut(sb, cap, nif.Nodes.Count - shown)) return;
            sb.Append("  ").Append(new string(' ', n.Depth * 2)).Append(n.Name.Length > 0 ? n.Name : "(unnamed)")
              .Append("  ").Append(DescribeFlags(n.Flags, n.FlagsDefault, n.FlagsDefaultType, n.BlockType)).Append('\n');
            shown++;
        }
    }

    static void RenderStrings(StringBuilder sb, NifInspect nif, int cap)
    {
        sb.Append("\n--- header string table (").Append(nif.HeaderStrings.Count).Append(") ---\n");
        int shown = 0;
        foreach (var s in nif.HeaderStrings)
        {
            if (Cut(sb, cap, nif.HeaderStrings.Count - shown)) return;
            sb.Append("  [").Append(shown).Append("] ").Append(s).Append('\n');
            shown++;
        }
        if (shown == 0) sb.Append("  (none)\n");
    }

    static string AlphaLine(NifAlpha a)
        => $"flags 0x{a.Flags:X4}  blend={a.Blend} ({a.SourceBlendMode} -> {a.DestinationBlendMode})  test={a.Test} ({a.TestFunction})  threshold {a.Threshold}";

    /// <summary>Render NiAVObject flags: raw hex + a by-construction decode. nif.xml does not name these bits, so we
    /// decode by DEVIATION from the type's nif.xml-documented SSE default (<paramref name="def"/> from
    /// <paramref name="defType"/>) — "= default", or the extra/missing bits vs it — and always state the one bit nif.xml
    /// DOES document, 0x80000 (Skyrim sets it on some AV objects, FO4 never; the head-vs-hair signal). When no default is
    /// documented for the type, the set-bit positions are listed instead (still exact, still no invented names).</summary>
    static string DescribeFlags(uint flags, uint? def, string? defType, string blockType)
    {
        var sb = new StringBuilder("0x").Append(flags.ToString("X"));
        sb.Append("  [0x80000 ").Append((flags & 0x80000) != 0 ? "set" : "clear");
        if (def is { } d)
        {
            if (flags == d) sb.Append("; = ").Append(defType).Append(" default");
            else
            {
                uint extra = flags & ~d, missing = d & ~flags;
                sb.Append("; vs ").Append(defType).Append(" default 0x").Append(d.ToString("X")).Append(':');
                if (extra != 0) sb.Append(" +0x").Append(extra.ToString("X"));
                if (missing != 0) sb.Append(" -0x").Append(missing.ToString("X"));
            }
        }
        else
            sb.Append("; no documented default for ").Append(blockType).Append("; bits ").Append(BitList(flags));
        return sb.Append(']').ToString();
    }

    /// <summary>The set bit positions of a 32-bit flags word, comma-joined (e.g. "1,2,3,26"), or "none".</summary>
    static string BitList(uint f)
    {
        var bits = new List<int>();
        for (int i = 0; i < 32; i++) if ((f & (1u << i)) != 0) bits.Add(i);
        return bits.Count == 0 ? "none" : string.Join(",", bits);
    }

    /// <summary>Append "<label><a, b, c>" as one line, cut with an explicit notice if it would blow the cap (Q3).</summary>
    static void AppendClampedList(StringBuilder sb, string label, IEnumerable<string> items, int cap)
    {
        sb.Append(label);
        var list = items.ToList();
        int shown = 0;
        for (; shown < list.Count; shown++)
        {
            if (sb.Length >= cap) break;
            if (shown > 0) sb.Append(", ");
            sb.Append(list[shown]);
        }
        if (shown < list.Count)
            sb.Append(" … [").Append(list.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]");
        sb.Append('\n');
    }

    /// <summary>True (and appends the cut notice) when the buffer has hit the cap mid-section — the per-item loop breaks.</summary>
    static bool Cut(StringBuilder sb, int cap, int remaining)
    {
        if (sb.Length < cap) return false;
        sb.Append("  … [").Append(Math.Max(remaining, 0)).Append(" more omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
        return true;
    }

    static string Fmt(float f) => f.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The archive-read-failure alarm (Q3): BSAs that couldn't be read this build. A mesh present ONLY in one of
    /// these is indistinguishable from a truly absent one, so it is surfaced LOUD before the answer.</summary>
    static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, int cap)
    {
        if (failures.Count == 0) return;
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — a mesh present only in these may read as ABSENT:\n");
        int shown = 0;
        foreach (var f in failures)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(failures.Count - shown).Append(" more omitted]\n"); break; }
            sb.Append("  - ").Append(f).Append('\n'); shown++;
        }
    }

    static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, int cap)
    {
        if (warnings.Count == 0) return;
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        int shown = 0;
        foreach (var w in warnings)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(warnings.Count - shown).Append(" more omitted]\n"); break; }
            sb.Append("  - ").Append(w).Append('\n'); shown++;
        }
    }
}

using System.Reflection.PortableExecutable;
using System.Text;

namespace HousecarlCore;

/// <summary>
/// Reads what an SKSE plugin DLL DECLARES about itself, STATICALLY — no loading, no execution, no runtime state.
/// It stops at the declared manifest; DLL behaviour is out of reach by design.
///
/// The <c>SKSE::PluginVersionData</c> layout is identical in both CommonLib lineages (alandtse/CommonLibVR@ng and
/// powerof3/CommonLibSSE@dev), and the SKSE loader fixes its ABI:
///
///   • The AE-era SKSE loader reads an exported DATA BLOB named <c>SKSEPlugin_Version</c> without executing the
///     DLL, so its bytes are a static manifest: plugin name, author, version, the version-independence flags
///     (Address Library / signature-scanning / struct-compat), the compatible-runtime list, and the XSE version
///     floor. That is what a modern plugin exports.
///   • The older SE/VR loader instead CALLS an exported FUNCTION <c>SKSEPlugin_Query</c> that fills its info at
///     runtime, so a query-ONLY (no <c>SKSEPlugin_Version</c>) plugin's metadata is NOT statically readable. It is
///     classified <see cref="SksePluginKind.LegacyQuery"/> and says so, rather than inventing a name.
///   • A DLL in SKSE\Plugins with NONE of the SKSE exports is a bundled dependency, not a plugin
///     (<see cref="SksePluginKind.NotSkse"/>) — e.g. CrashLogger's msdia140.dll.
///
/// The struct layout is load-bearing. The one non-obvious detail is <c>supportEmail[252]</c> (NOT 256), which puts
/// <c>versionIndependenceEx</c> at 0x304, not 0x308. See <see cref="DecodeVersionBlob"/> for the offset map. This
/// reader never guesses a layout — the offsets come from the pinned headers.
/// </summary>
public static class SksePluginReader
{
    /// <summary>How a DLL under SKSE\Plugins relates to the SKSE plugin ABI. Drives what metadata (if any) is readable.</summary>
    public enum SksePluginKind
    {
        /// <summary>Exports the <c>SKSEPlugin_Version</c> data blob → its metadata is statically readable.</summary>
        Modern,
        /// <summary>Exports <c>SKSEPlugin_Query</c>/<c>SKSEPlugin_Load</c> but NO version blob — an SE/VR-era plugin whose
        /// metadata is filled at runtime, so it is not statically readable. Named, never silently degraded.</summary>
        LegacyQuery,
        /// <summary>No SKSE export at all — a bundled dependency DLL, not a plugin.</summary>
        NotSkse,
        /// <summary>Not a readable PE image (corrupt / not actually a DLL). Surfaced, never silently skipped.</summary>
        Unreadable,
    }

    /// <summary>The statically-declared manifest of a MODERN plugin (the <c>SKSEPlugin_Version</c> blob), decoded.
    /// <see cref="CompatibleVersions"/> is the loader's hard runtime list and is meaningful ONLY when
    /// <see cref="VersionIndependent"/> is false — a version-independent plugin's loader ignores it (some builds pad it
    /// with 1.0.0 noise), so the renderer suppresses it there.</summary>
    public sealed record SkseVersionInfo(
        string Name,
        string Author,
        string SupportEmail,
        string PluginVersion,
        bool UsesAddressLibrary,
        bool UsesSignatureScanning,
        bool UsesUpdatedStructs,
        bool DeclaresNoStructs,
        IReadOnlyList<string> CompatibleVersions,
        string? MinimumXseVersion)
    {
        /// <summary>True if the plugin declared ANY version-independence path (Address Library or signature scanning),
        /// so the loader does NOT pin it to <see cref="CompatibleVersions"/>. False ⇒ version-LOCKED: it loads only on
        /// the exact runtimes it lists, which is the compatibility risk worth surfacing.</summary>
        public bool VersionIndependent => UsesAddressLibrary || UsesSignatureScanning;
    }

    /// <summary>One DLL's static SKSE identity. <see cref="Version"/> is non-null only for <see cref="SksePluginKind.Modern"/>.
    /// <see cref="Is64Bit"/> is <c>null</c> when the COFF machine field was never read (a non-PE / unopenable file whose
    /// bitness is genuinely UNKNOWN — it is NEVER presented as 32-bit on a guess). <see cref="Note"/> carries the reason
    /// for any non-Modern kind: why the metadata isn't readable.</summary>
    public sealed record SksePluginInfo(
        string FileName,
        SksePluginKind Kind,
        bool? Is64Bit,
        SkseVersionInfo? Version,
        string? Note,
        IReadOnlyList<string>? Imports = null)
    {
        /// <summary>The DLL names this image statically imports — import AND delay-load directories, lower-cased and
        /// deduplicated. Tri-state on purpose, like <see cref="Is64Bit"/>: a NON-EMPTY list is what it imports; EMPTY
        /// means the directories were walked and it genuinely imports nothing; <c>null</c> means the walk never
        /// happened or FAILED (no optional header / corrupt directory), an UNKNOWN that must never render as "imports
        /// nothing". Populated on EVERY read because it rides the PE open the manifest read already pays for, which is
        /// what lets the Debug-CRT check run over the whole layer. Reading the image's STRINGS is far dearer and stays
        /// opt-in per-DLL: see <see cref="SksePeek"/>.</summary>
        public IReadOnlyList<string>? Imports { get; init; } = Imports;

        /// <summary>The debug-CRT DLLs this image imports — empty when it imports none, or when the walk failed (check
        /// <see cref="Imports"/> for null before reading absence as proof). See <see cref="DebugCrtImportsOf"/>.</summary>
        public IReadOnlyList<string> DebugCrtImports => DebugCrtImportsOf(this);
    }

    /// <summary>Read one DLL's static SKSE manifest off disk. Never throws for a bad or odd file — an unreadable image
    /// is reported as <see cref="SksePluginKind.Unreadable"/> with the reason. Reads with a read-share stream and
    /// closes it before returning, so no handle is held at rest and MO2/xEdit can still move the file.</summary>
    public static SksePluginInfo Read(string filePath)
    {
        string file = Path.GetFileName(filePath);
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadStream(file, fs);
        }
        catch (BadImageFormatException ex)
        {
            return new SksePluginInfo(file, SksePluginKind.Unreadable, null, null, $"not a valid PE image: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SksePluginInfo(file, SksePluginKind.Unreadable, null, null, $"could not read: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Read a DLL's static SKSE manifest from in-memory bytes — the BSA-packed twin of <see cref="Read"/>, so
    /// an archive-shipped non-SKSE dependency is screened out the same way a loose one is. Same never-throws
    /// contract.</summary>
    public static SksePluginInfo ReadBytes(string fileName, byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return ReadStream(fileName, ms);
        }
        catch (BadImageFormatException ex)
        {
            return new SksePluginInfo(fileName, SksePluginKind.Unreadable, null, null, $"not a valid PE image: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SksePluginInfo(fileName, SksePluginKind.Unreadable, null, null, $"could not read: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The shared decode over any seekable stream (the two entry points above own the never-throws catch).</summary>
    static SksePluginInfo ReadStream(string file, Stream stream)
    {
        using var pe = new PEReader(stream);
        // The COFF machine field is available as soon as the PE opened — read the real bitness even when the optional
        // header is missing, so an Unreadable-with-no-optional-header still reports a TRUE x64/x86 rather than a
        // fabricated one. Only the catch paths below (which never got this far) leave bitness null = UNKNOWN.
        bool is64 = pe.PEHeaders.CoffHeader.Machine == Machine.Amd64;
        if (pe.PEHeaders.PEHeader is null)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null, "no PE optional header");

        // Walk the imports BEFORE the export classification, so even a DLL that goes on to be Unreadable (corrupt EAT)
        // still reports what it imports — the Debug-CRT verdict does not depend on the SKSE manifest being readable.
        var imports = ReadImportNames(pe);

        var exports = ReadExportRvas(pe);
        if (exports is null)   // export directory present but CORRUPT — a parse failure, not "no exports": a corrupt DLL must not classify as a bundled dependency
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null, "corrupt PE export directory — could not enumerate exports", imports);

        bool hasVersion = exports.TryGetValue("SKSEPlugin_Version", out int versionRva) && versionRva != 0;
        bool hasQuery = exports.ContainsKey("SKSEPlugin_Query");
        bool hasLoad = exports.ContainsKey("SKSEPlugin_Load") || exports.ContainsKey("SKSEPlugin_Preload");

        if (!hasVersion && !hasQuery && !hasLoad)
            return new SksePluginInfo(file, SksePluginKind.NotSkse, is64, null,
                "no SKSE export (SKSEPlugin_Version/Query/Load) — a bundled dependency DLL, not a plugin", imports);

        if (!hasVersion)
            return new SksePluginInfo(file, SksePluginKind.LegacyQuery, is64, null,
                "legacy SE/VR plugin: exports SKSEPlugin_Query (metadata is filled at runtime), so name/version are not statically readable", imports);

        // Modern: slice the version blob out of its section and decode. A real SKSEPluginVersionData is a FULL
        // 0x350-byte struct whose dataVersion (0x000) is kVersion (>= 1); a version export whose RVA maps to no
        // section, a forwarder, or a corrupt EAT yields a short or all-zero blob — say so rather than present a
        // phantom "" v0.0.0 plugin.
        var block = pe.GetSectionData(versionRva);
        byte[] blob = block.GetReader().ReadBytes(Math.Min(0x350, block.Length));
        if (blob.Length < 0x350 || BitConverter.ToUInt32(blob, 0) == 0)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null,
                "exports SKSEPlugin_Version but its RVA does not resolve to a readable version blob (a forwarded or corrupt export)", imports);
        var ver = DecodeVersionBlob(blob);
        return new SksePluginInfo(file, SksePluginKind.Modern, is64, ver, null, imports);
    }

    /// <summary>Decode the raw <c>SKSEPlugin_Version</c> blob bytes into the manifest. Pure and bounds-checked, so the
    /// offset map can be pinned without a real PE. The layout (identical in alandtse/CommonLibVR@ng and
    /// powerof3/CommonLibSSE@dev, ABI-fixed by the SKSE loader):
    /// <code>
    /// 0x000 uint32  dataVersion
    /// 0x004 uint32  pluginVersion          (REL::Version.pack: maj&lt;&lt;24 | min&lt;&lt;16 | patch&lt;&lt;4 | build)
    /// 0x008 char    pluginName[256]
    /// 0x108 char    author[256]
    /// 0x208 char    supportEmail[252]      &lt;-- 252, NOT 256 (the one non-obvious offset)
    /// 0x304 uint32  versionIndependenceEx  (bit0 = NoStructUse)
    /// 0x308 uint32  versionIndependence    (bit0 = AddressLibraryPostAE, bit1 = Signatures, bit2 = StructsPost629)
    /// 0x30C uint32  compatibleVersions[16] (zero-terminated list of REL::Version.pack values)
    /// 0x34C uint32  xseMinimum
    /// </code></summary>
    public static SkseVersionInfo DecodeVersionBlob(ReadOnlySpan<byte> b)
    {
        uint pluginVersion = U32(b, 0x004);
        string name = AsciiZ(b, 0x008, 256);
        string author = AsciiZ(b, 0x108, 256);
        string email = AsciiZ(b, 0x208, 252);
        uint viEx = U32(b, 0x304);
        uint vi = U32(b, 0x308);
        var compat = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            uint packed = U32(b, 0x30C + i * 4);
            if (packed == 0) break;                     // zero-terminated list
            compat.Add(UnpackVersion(packed));
        }
        uint xseMin = U32(b, 0x34C);

        return new SkseVersionInfo(
            Name: name,
            Author: author,
            SupportEmail: email,
            PluginVersion: UnpackVersion(pluginVersion),
            UsesAddressLibrary: (vi & 0x1) != 0,        // kVersionIndependent_AddressLibraryPostAE
            UsesSignatureScanning: (vi & 0x2) != 0,     // kVersionIndependent_Signatures
            UsesUpdatedStructs: (vi & 0x4) != 0,        // kVersionIndependent_StructsPost629
            DeclaresNoStructs: (viEx & 0x1) != 0,       // kVersionIndependentEx_NoStructUse
            CompatibleVersions: compat,
            MinimumXseVersion: xseMin == 0 ? null : UnpackVersion(xseMin));
    }

    /// <summary>Whether a MODERN plugin can load on <paramref name="installedRuntime"/> (a dotted game version, e.g.
    /// "1.6.1170.0" from the executable's version resource). Version-independent plugins load anywhere → true;
    /// a version-LOCKED plugin loads only when a listed compatible runtime matches numerically. Numeric,
    /// zero-padded segment compare — "1.6.1170" and "1.6.1170.0" are the SAME version (the blob lists 3 segments,
    /// the exe resource 4). Pure.</summary>
    public static bool RuntimeCompatible(SkseVersionInfo v, string installedRuntime)
        => v.VersionIndependent || v.CompatibleVersions.Any(cv => VersionsEqual(cv, installedRuntime));

    /// <summary>True when the dotted runtime version is AE-era (1.6 or later). Load-bearing for LegacyQuery
    /// adjudication: the AE SKSE loader loads ONLY plugins exporting the <c>SKSEPlugin_Version</c> data blob, so a
    /// query-only SE/VR-era plugin will NOT load on an AE runtime. A non-numeric or short version returns FALSE —
    /// unknown never becomes a "won't load" claim.</summary>
    public static bool IsAeRuntime(string runtime)
    {
        var seg = runtime.Split('.');
        if (seg.Length < 2 || !int.TryParse(seg[0].Trim(), out var maj) || !int.TryParse(seg[1].Trim(), out var min))
            return false;
        return maj > 1 || (maj == 1 && min >= 6);
    }

    /// <summary>The DEBUG C-runtime DLLs — a plugin importing any of these was built in a Debug configuration and shipped
    /// that way. CURATED, and curated on purpose: the D-suffix is a naming CONVENTION, not a loader rule, so "ends in d.dll"
    /// would sweep in innocents (dinput8.dll, d3d11.dll, and every mod DLL ending in 'd'). This is the exact
    /// Microsoft debug-CRT family — a pinned list, never a guessed pattern.
    ///
    /// Why it is load-bearing: these DLLs are NOT redistributable — they ship only with Visual Studio and are absent
    /// from a stock Windows install. A plugin importing one fails to load with error 126 (ERROR_MOD_NOT_FOUND) on any
    /// machine without VS, so it works for its author and is dead for every user.</summary>
    public static readonly IReadOnlyList<string> DebugCrtDlls =
    [
        "ucrtbased.dll",                                                   // the debug universal CRT
        "vcruntime140d.dll", "vcruntime140_1d.dll",                        // VC++ 2015-2022 debug runtime (_1 = the x64 EH half)
        "msvcp140d.dll", "msvcp140_1d.dll", "msvcp140_2d.dll",             // debug C++ standard library
        "msvcp140d_atomic_wait.dll", "msvcp140_codecvt_ids_d.dll",         // its split-out debug companions
        "concrt140d.dll",                                                  // debug Concurrency Runtime
        "mfc140d.dll", "mfc140ud.dll",                                     // debug MFC (rare in plugins, real in tooling DLLs)
        "msvcr120d.dll", "msvcp120d.dll",                                  // VC++ 2013 debug runtime (pre-CommonLib-era plugins)
        "msvcr110d.dll", "msvcp110d.dll",                                  // VC++ 2012
        "msvcr100d.dll", "msvcp100d.dll",                                  // VC++ 2010
    ];

    /// <summary>The debug-CRT DLLs <paramref name="info"/> imports, in the order the image lists them. EMPTY when it
    /// imports none — and ALSO empty when <see cref="SksePluginInfo.Imports"/> is null (the walk failed), because absence
    /// of evidence is not evidence of absence: a caller rendering a "clean" verdict must check <c>Imports is not null</c>
    /// first. Pure.</summary>
    public static IReadOnlyList<string> DebugCrtImportsOf(SksePluginInfo info) =>
        info.Imports is null ? []
            : info.Imports.Where(i => DebugCrtDlls.Contains(i, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>The load-blocker reason when <paramref name="info"/> is a DEBUG build whose debug runtime is ABSENT from
    /// this machine — else <c>null</c>. One more static way an SKSE DLL fails to load, alongside BSA-only, subfolder,
    /// 32-bit, unreadable, version-locked and query-only-on-AE: a debug-built DLL is loose, top-level, x64, readable
    /// and often version-INDEPENDENT, so every other check passes it as healthy while the loader refuses it with error
    /// 126 and scripts declaring its natives become silent no-ops.
    ///
    /// Returns null when the runtime IS present (a developer's box): there the DLL genuinely loads, so there is no
    /// blocker to claim, though the inventory still names it as broken for everyone else. Also null when the import
    /// walk failed (<see cref="SksePluginInfo.Imports"/> is null) — absence of evidence is not evidence of absence.
    ///
    /// <paramref name="resolvable"/> is injected so both outcomes can be exercised on one machine: a hard-wired lookup
    /// would leave whichever half the current machine cannot produce untested. Pure.</summary>
    public static string? DebugCrtBlocker(SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Imports is null) return null;
        var missing = DebugCrtImportsOf(info).Where(c => !resolvable(c)).ToList();
        return missing.Count == 0 ? null
            : $"a DEBUG build — it imports {string.Join(", ", missing)}, which ships only with Visual Studio and is not " +
              "present on this machine, so the loader fails with error 126 (ERROR_MOD_NOT_FOUND)";
    }

    /// <summary>Whether <paramref name="dll"/> is resolvable by the Windows loader ON THIS MACHINE — the honest other half
    /// of the Debug-CRT verdict. "Imports the debug CRT ⇒ will not load" is true only where the debug CRT is ABSENT, which
    /// is every stock machine but NOT a developer's (Visual Studio installs it). houseCARL runs on the modder's own box, so
    /// it can check instead of assuming: a flat "will NOT load" on a machine that has the runtime is a confidently
    /// wrong answer, and a modder who authors SKSE plugins is precisely the user who has VS.
    ///
    /// Approximates the loader's search order for a DLL loaded from the game root: System32, then the PATH directories.
    /// (The debug CRT is never in the game root and never side-by-side for a mod DLL.) Conservative by construction — it
    /// answers "is this findable here", and a false NEGATIVE only downgrades a claim to the safer machine-specific one.</summary>
    public static bool IsSystemDllResolvable(string dll) => _resolvableMemo.GetOrAdd(dll, static d =>
    {
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (sys.Length > 0 && File.Exists(Path.Combine(sys, d))) return true;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                try { if (File.Exists(Path.Combine(dir.Trim(), d))) return true; }
                catch { /* a malformed PATH entry is not an answer — keep looking */ }
            }
        }
        catch { /* environment unreadable → fall through to "not resolvable", the machine-specific (safer) claim */ }
        return false;
    });

    /// <summary>Memo for <see cref="IsSystemDllResolvable"/>: System32 + PATH are machine-static for the process's life,
    /// so the stat walk is worth exactly one run per name. (Concurrent because tool calls are.)</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _resolvableMemo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numeric dotted-version equality with zero-padding ("1.6.1170" == "1.6.1170.0"). A non-numeric
    /// segment ⇒ NOT equal, never guessed equal: a garbage compat entry must not accidentally PASS a lock.</summary>
    public static bool VersionsEqual(string a, string b)
    {
        var sa = a.Split('.'); var sb = b.Split('.');
        for (int i = 0; i < Math.Max(sa.Length, sb.Length); i++)
        {
            int va = 0, vb = 0;
            if (i < sa.Length && !int.TryParse(sa[i].Trim(), out va)) return false;
            if (i < sb.Length && !int.TryParse(sb[i].Trim(), out vb)) return false;
            if (va != vb) return false;
        }
        return true;
    }

    /// <summary>Unpack a <c>REL::Version</c> uint32 to "maj.min.patch[.build]" — the exact CommonLib packing
    /// (maj 8b &lt;&lt; 24 | min 8b &lt;&lt; 16 | patch 12b &lt;&lt; 4 | build 4b). Trailing .build is shown only when non-zero
    /// (most plugins declare only maj[.min.patch]).</summary>
    public static string UnpackVersion(uint v)
    {
        int major = (int)((v >> 24) & 0xFF);
        int minor = (int)((v >> 16) & 0xFF);
        int patch = (int)((v >> 4) & 0xFFF);
        int build = (int)(v & 0xF);
        return build != 0 ? $"{major}.{minor}.{patch}.{build}" : $"{major}.{minor}.{patch}";
    }

    static uint U32(ReadOnlySpan<byte> b, int off) =>
        off + 4 <= b.Length ? BitConverter.ToUInt32(b.Slice(off, 4)) : 0u;

    /// <summary>Read an ASCII, null-terminated field of at most <paramref name="max"/> bytes at <paramref name="off"/>.
    /// Trailing non-printables are trimmed defensively (a garbage blob never yields control chars in the render).</summary>
    static string AsciiZ(ReadOnlySpan<byte> buf, int off, int max)
    {
        if (off >= buf.Length) return "";
        int limit = Math.Min(off + max, buf.Length);
        int end = off;
        while (end < limit && buf[end] != 0) end++;
        var sb = new StringBuilder(end - off);
        for (int i = off; i < end; i++)
        {
            byte c = buf[i];
            sb.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');   // printable ASCII only; others → space, so no control chars leak into output
        }
        return sb.ToString().Trim();
    }

    /// <summary>Walk the PE IMPORT + DELAY-LOAD directories and return the imported DLL names, lower-cased and
    /// deduplicated, in image order. Same rule as <see cref="ReadExportRvas"/>: an ABSENT directory yields an empty
    /// list (a real, if odd, "imports nothing"), while a PRESENT-but-CORRUPT one yields <c>null</c> — a parse failure
    /// the caller renders as UNKNOWN, since a corrupt import table must not read as a clean bill of health.
    /// Never throws.
    ///
    /// Both directories are arrays of fixed-size descriptors terminated by an all-zero entry, each carrying an RVA to the
    /// imported DLL's ASCII name. Delay-load descriptors predate the RVA convention: bit0 of their Attributes is
    /// <c>RvaBased</c>, and a (long-obsolete) VA-based table is SKIPPED rather than misread as an RVA.</summary>
    static List<string>? ReadImportNames(PEReader pe)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdr = pe.PEHeaders.PEHeader!;
        // Bound the descriptor count against corruption BEFORE looping: a real DLL imports from at most a few hundred
        // modules, so a table that never terminates is corrupt — refuse it rather than spin.
        const int MaxDescriptors = 4096;

        bool ok = Walk(hdr.ImportTableDirectory.RelativeVirtualAddress, hdr.ImportTableDirectory.Size, 20, 0x0C, delay: false)
               && Walk(hdr.DelayImportTableDirectory.RelativeVirtualAddress, hdr.DelayImportTableDirectory.Size, 32, 0x04, delay: true);
        return ok ? names : null;

        bool Walk(int dirRva, int dirSize, int stride, int nameOff, bool delay)
        {
            if (dirSize == 0 || dirRva == 0) return true;              // directory genuinely absent → nothing to add
            try
            {
                var block = pe.GetSectionData(dirRva);
                if (block.Length == 0) return false;                   // directory declared but maps to no section → corrupt
                // Bound by what the HEADER declares, not merely by the section remainder: an unterminated table would
                // otherwise read on into adjacent .rdata and invent descriptors out of unrelated bytes.
                int limit = Math.Min(dirSize, block.Length);
                var rd = block.GetReader();
                for (int i = 0; i < MaxDescriptors; i++)
                {
                    if ((i + 1) * stride > limit) return false;        // table runs past its declared size without terminating → corrupt
                    rd.Offset = i * stride;
                    uint attributes = delay ? rd.ReadUInt32() : 0;     // delay-load: Attributes precedes the name RVA
                    rd.Offset = i * stride + nameOff;
                    int nameRva = rd.ReadInt32();
                    // The all-zero descriptor terminates the array. A delay-load table whose bit0 (RvaBased) is clear is
                    // VA-based (pre-VS2015); its "RVA" is an absolute address we must NOT resolve — skip, don't guess.
                    if (nameRva == 0) return true;
                    if (delay && (attributes & 1) == 0) continue;
                    // An unresolvable name is CORRUPTION, and skipping it would hand back a short list that renders as a
                    // complete "imports (N): …" — a silent partial answer, which is worse here than no answer: if the
                    // entry we dropped were vcruntime140d.dll, the Debug-CRT check would report a clean bill of health.
                    // Fail the whole walk (→ null → UNKNOWN), consistent with this method's directory-level posture.
                    if (ReadAsciiAt(pe, nameRva) is not { Length: > 0 } n) return false;
                    if (seen.Add(n)) names.Add(n);                     // a DUPLICATE name is normal dedup, not a failure
                }
                return false;                                          // never hit the terminator inside the bound → corrupt
            }
            catch { return false; /* bad RVA / truncated table / unterminated string → parse failure, not an empty answer */ }
        }
    }

    /// <summary>Read a null-terminated ASCII string at an RVA (an imported DLL's name), lower-cased for comparison.
    /// Bounded — a name is short, and an unterminated run means corruption, so it stops rather than reading a section.</summary>
    static string? ReadAsciiAt(PEReader pe, int rva)
    {
        var block = pe.GetSectionData(rva);
        if (block.Length == 0) return null;
        var rd = block.GetReader();
        var sb = new StringBuilder(24);
        const int MaxName = 260;                                       // MAX_PATH; a real module name is far shorter
        for (int i = 0; i < MaxName && i < block.Length; i++)
        {
            byte c = rd.ReadByte();
            if (c == 0) return sb.ToString().ToLowerInvariant();
            if (c is < 0x20 or > 0x7E) return null;                    // a non-printable inside a module name ⇒ not a real name
            sb.Append((char)c);
        }
        return null;                                                   // unterminated → corrupt, never a truncated guess
    }

    /// <summary>Walk the PE export directory and return name → export RVA (data exports point AT the data). Minimal by
    /// design: the SKSE loader itself resolves symbols by exact unmangled name string, so a name lookup is all we need.
    /// Returns an EMPTY map for a DLL with genuinely no export table (→ classify NotSkse), but <c>null</c> when the directory
    /// is present yet CORRUPT (a parse failure — the caller classifies Unreadable, never silently as a bundled
    /// dependency). Never throws.</summary>
    static Dictionary<string, int>? ReadExportRvas(PEReader pe)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (dir.Size == 0 || dir.RelativeVirtualAddress == 0) return byName;   // no export table → empty (genuinely no exports)
        try
        {
            var ed = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
            ed.Offset = 0;
            ed.ReadUInt32();                 // Characteristics
            ed.ReadUInt32();                 // TimeDateStamp
            ed.ReadUInt16(); ed.ReadUInt16();// Major/Minor version
            ed.ReadUInt32();                 // Name RVA
            ed.ReadUInt32();                 // OrdinalBase
            uint numFuncs = ed.ReadUInt32(); // AddressOfFunctions count
            uint numNames = ed.ReadUInt32(); // AddressOfNames count
            int eatRva = ed.ReadInt32();     // AddressOfFunctions
            int nameRva = ed.ReadInt32();    // AddressOfNames
            int ordRva = ed.ReadInt32();     // AddressOfNameOrdinals

            // Bound the counts against corruption BEFORE allocating/looping: a real DLL exports at most a few thousand
            // symbols, so a bogus count means a corrupt directory. Trusting it would OOM `new int[numFuncs]` or spin the
            // loops — refuse it as a parse failure (null → Unreadable), never trust a corruption-controlled length.
            const uint MaxExports = 65536;
            if (numFuncs > MaxExports || numNames > MaxExports) return null;

            var eat = pe.GetSectionData(eatRva).GetReader();
            var funcRvas = new int[numFuncs];
            for (int i = 0; i < numFuncs; i++) funcRvas[i] = eat.ReadInt32();

            var nameTab = pe.GetSectionData(nameRva).GetReader();
            var ordTab = pe.GetSectionData(ordRva).GetReader();
            for (int i = 0; i < numNames; i++)
            {
                int strRva = nameTab.ReadInt32();
                var sr = pe.GetSectionData(strRva).GetReader();
                var sb = new StringBuilder(32);
                byte c;
                while ((c = sr.ReadByte()) != 0) sb.Append((char)c);
                ushort ord = ordTab.ReadUInt16();
                byName[sb.ToString()] = ord < funcRvas.Length ? funcRvas[ord] : 0;
            }
        }
        catch { return null; /* corrupt directory (bad RVA / truncated table / unterminated string) → parse-failure signal, NOT a silent empty map that would misclassify as NotSkse */ }
        return byName;
    }
}

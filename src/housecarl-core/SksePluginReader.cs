using System.Reflection.PortableExecutable;
using System.Text;

namespace HousecarlCore;

/// <summary>Reads what an SKSE plugin DLL DECLARES about itself, statically — no loading, no execution, no runtime
/// state. The blob layout, the kinds and the load rule are in docs/architecture/skse-layer.md.</summary>
public static class SksePluginReader
{
    public enum SksePluginKind
    {
        /// <summary>Exports the <c>SKSEPlugin_Version</c> data blob → its metadata is statically readable.</summary>
        Modern,
        /// <summary>SE/VR-era: no version blob, so its metadata is filled at runtime — named, never silently degraded.</summary>
        LegacyQuery,
        /// <summary>No SKSE export at all — a bundled dependency DLL, not a plugin.</summary>
        NotSkse,
        /// <summary>Not a readable PE image (corrupt / not actually a DLL). Surfaced, never silently skipped.</summary>
        Unreadable,
    }

    /// <summary>The decoded manifest of a MODERN plugin; <see cref="CompatibleVersions"/> is meaningful only when <see cref="VersionIndependent"/> is false.</summary>
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
        /// <summary>True if the plugin declared any version-independence path; false ⇒ version-LOCKED to its listed runtimes.</summary>
        public bool VersionIndependent => UsesAddressLibrary || UsesSignatureScanning;
    }

    /// <summary>One DLL's static SKSE identity; <see cref="Is64Bit"/> is tri-state — the never-guess rule is in docs/architecture/skse-layer.md.</summary>
    public sealed record SksePluginInfo(
        string FileName,
        SksePluginKind Kind,
        bool? Is64Bit,
        SkseVersionInfo? Version,
        string? Note,
        IReadOnlyList<string>? Imports = null,
        string? FileVersion = null)
    {
        /// <summary>The build-stamped Win32 file version, a second and independent number to the manifest's own.</summary>
        public string? FileVersion { get; init; } = FileVersion;

        /// <summary>The DLL names this image imports (import AND delay-load), tri-state: <c>null</c> is a walk that
        /// never happened or failed and must never render as "imports nothing".</summary>
        public IReadOnlyList<string>? Imports { get; init; } = Imports;

        /// <summary>The debug-CRT DLLs this image imports — also empty when the walk failed, so check <see cref="Imports"/> for null.</summary>
        public IReadOnlyList<string> DebugCrtImports => DebugCrtImportsOf(this);
    }

    /// <summary>Read one DLL's static SKSE manifest off disk — never throws, and holds no handle at rest.</summary>
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

    /// <summary>The BSA-packed twin of <see cref="Read"/>, reading from bytes; same never-throws contract.</summary>
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
        // Read off the COFF header, so an Unreadable-with-no-optional-header still reports a TRUE bitness.
        bool is64 = pe.PEHeaders.CoffHeader.Machine == Machine.Amd64;
        if (pe.PEHeaders.PEHeader is null)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null, "no PE optional header");

        // Before the export classification, so an Unreadable DLL still reports what it imports.
        var imports = ReadImportNames(pe);
        var fileVersion = ReadFileVersionResource(pe);

        var exports = ReadExportRvas(pe);
        if (exports is null)   // export directory present but CORRUPT — a parse failure, not "no exports": a corrupt DLL must not classify as a bundled dependency
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null, "corrupt PE export directory — could not enumerate exports", imports, fileVersion);

        bool hasVersion = exports.TryGetValue("SKSEPlugin_Version", out int versionRva) && versionRva != 0;
        bool hasQuery = exports.ContainsKey("SKSEPlugin_Query");
        bool hasLoad = exports.ContainsKey("SKSEPlugin_Load") || exports.ContainsKey("SKSEPlugin_Preload");

        if (!hasVersion && !hasQuery && !hasLoad)
            return new SksePluginInfo(file, SksePluginKind.NotSkse, is64, null,
                "no SKSE export (SKSEPlugin_Version/Query/Load) — a bundled dependency DLL, not a plugin", imports, fileVersion);

        if (!hasVersion)
            return new SksePluginInfo(file, SksePluginKind.LegacyQuery, is64, null,
                "legacy SE/VR plugin: exports SKSEPlugin_Query (metadata is filled at runtime), so name/version are not statically readable", imports, fileVersion);

        // Modern: slice the version blob out of its section and decode; a short or all-zero blob is named, never decoded.
        var block = pe.GetSectionData(versionRva);
        byte[] blob = block.GetReader().ReadBytes(Math.Min(0x350, block.Length));
        if (blob.Length < 0x350 || BitConverter.ToUInt32(blob, 0) == 0)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null,
                "exports SKSEPlugin_Version but its RVA does not resolve to a readable version blob (a forwarded or corrupt export)", imports, fileVersion);
        var ver = DecodeVersionBlob(blob);
        return new SksePluginInfo(file, SksePluginKind.Modern, is64, ver, null, imports, fileVersion);
    }

    /// <summary>Decode the raw <c>SKSEPlugin_Version</c> blob into the manifest, pure and bounds-checked; the offset
    /// map is in docs/architecture/skse-layer.md and pinned by SkseReaderProbe arms A and F.</summary>
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

    /// <summary>Whether a MODERN plugin can load on <paramref name="installedRuntime"/> — a numeric, zero-padded compare. Pure.</summary>
    public static bool RuntimeCompatible(SkseVersionInfo v, string installedRuntime)
        => v.VersionIndependent || v.CompatibleVersions.Any(cv => VersionsEqual(cv, installedRuntime));

    /// <summary>True when the dotted runtime version is AE-era (1.6+) — the query-only-on-AE arm of the load rule; a
    /// non-numeric version returns FALSE, so unknown never becomes a "won't load" claim.</summary>
    public static bool IsAeRuntime(string runtime)
    {
        var seg = runtime.Split('.');
        if (seg.Length < 2 || !int.TryParse(seg[0].Trim(), out var maj) || !int.TryParse(seg[1].Trim(), out var min))
            return false;
        return maj > 1 || (maj == 1 && min >= 6);
    }

    /// <summary>The DEBUG C-runtime DLLs — the exact Microsoft family, CURATED because the d-suffix is a convention and
    /// not a loader rule; pinned by SkseImportWalkTests.TheDebugCrtListCarriesTheModernDebugFamily, and the innocents it
    /// must not sweep in by SkseImportWalkTests.TheDebugCrtListLeavesOutReleaseTwinsAndDSuffixedInnocents.</summary>
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

    /// <summary>The debug-CRT DLLs <paramref name="info"/> imports — also empty when the walk failed, so a clean verdict must check <c>Imports</c> first. Pure.</summary>
    public static IReadOnlyList<string> DebugCrtImportsOf(SksePluginInfo info) =>
        info.Imports is null ? []
            : info.Imports.Where(i => DebugCrtDlls.Contains(i, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>The Debug-CRT arm of the static-load rule (docs/architecture/skse-layer.md): the blocker reason when
    /// this is a debug build whose runtime is absent here, else null. <paramref name="resolvable"/> is injected so both
    /// outcomes are reachable on one machine; pinned by SkseImportWalkTests.AnAbsentDebugRuntimeIsABlockerNamingTheDllAndError126
    /// and SkseImportWalkTests.APresentDebugRuntimeIsNoBlocker.</summary>
    public static string? DebugCrtBlocker(SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Imports is null) return null;
        var missing = DebugCrtImportsOf(info).Where(c => !resolvable(c)).ToList();
        return missing.Count == 0 ? null
            : $"a DEBUG build — it imports {string.Join(", ", missing)}, which ships only with Visual Studio and is not " +
              "present on this machine, so the loader fails with error 126 (ERROR_MOD_NOT_FOUND)";
    }

    /// <summary>Whether <paramref name="dll"/> is resolvable by the Windows loader ON THIS MACHINE — System32 then PATH; a false negative only downgrades the claim.</summary>
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

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _resolvableMemo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numeric dotted-version equality with zero-padding; a non-numeric segment is NOT equal, never guessed equal.</summary>
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

    /// <summary>Unpack a <c>REL::Version</c> uint32 to "maj.min.patch[.build]"; .build is shown only when non-zero.</summary>
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

    /// <summary>Walk the PE IMPORT + DELAY-LOAD directories for the imported DLL names; an absent directory yields an
    /// empty list, a present-but-corrupt one <c>null</c> = UNKNOWN. Never throws. Contract in
    /// docs/architecture/skse-layer.md; pinned by SkseImportWalkTests.AManagedAssemblyWalksEmptyNotUnknown and
    /// SkseImportWalkTests.AnUnresolvableImportNameFailsTheWholeWalk.</summary>
    static List<string>? ReadImportNames(PEReader pe)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdr = pe.PEHeaders.PEHeader!;
        // Bound the descriptor count against corruption BEFORE looping, so an unterminated table cannot spin.
        const int MaxDescriptors = 4096;

        bool ok = Walk(hdr.ImportTableDirectory.RelativeVirtualAddress, hdr.ImportTableDirectory.Size, 20, 0x0C, delay: false)
               && Walk(hdr.DelayImportTableDirectory.RelativeVirtualAddress, hdr.DelayImportTableDirectory.Size, 32, 0x04, delay: true);
        return ok ? names : null;

        bool Walk(int dirRva, int dirSize, int stride, int nameOff, bool delay)
        {
            if (dirRva == 0) return true;                              // directory genuinely absent → nothing to add
            // A declared RVA with a zero Size is NOT absence, and Size is this walk's only bound on the table: UNKNOWN.
            if (dirSize == 0) return false;
            try
            {
                var block = pe.GetSectionData(dirRva);
                if (block.Length == 0) return false;                   // directory declared but maps to no section → corrupt
                // Bound by what the HEADER declares, not by the section remainder, so no descriptor is invented from adjacent .rdata.
                int limit = Math.Min(dirSize, block.Length);
                var rd = block.GetReader();
                for (int i = 0; i < MaxDescriptors; i++)
                {
                    if ((i + 1) * stride > limit) return false;        // table runs past its declared size without terminating → corrupt
                    rd.Offset = i * stride;
                    uint attributes = delay ? rd.ReadUInt32() : 0;     // delay-load: Attributes precedes the name RVA
                    rd.Offset = i * stride + nameOff;
                    int nameRva = rd.ReadInt32();
                    // The all-zero descriptor terminates the array.
                    if (nameRva == 0) return true;
                    // A delay-load table with bit0 (RvaBased) clear is VA-based, so resolve it as the loader does: VA minus image base.
                    if (delay && (attributes & 1) == 0)
                    {
                        long fromBase = (uint)nameRva - (long)hdr.ImageBase;
                        if (fromBase is <= 0 or > int.MaxValue) return false;
                        nameRva = (int)fromBase;
                    }
                    // An unresolvable name fails the WHOLE walk (→ UNKNOWN): a short list would render as a complete one.
                    if (ReadAsciiAt(pe, nameRva) is not { Length: > 0 } n) return false;
                    if (seen.Add(n)) names.Add(n);                     // a DUPLICATE name is normal dedup, not a failure
                }
                return false;                                          // never hit the terminator inside the bound → corrupt
            }
            catch { return false; /* bad RVA / truncated table / unterminated string → parse failure, not an empty answer */ }
        }
    }

    /// <summary>The image's Win32 version resource as "maj.min.build.rev", walked type (RT_VERSION = 16) → name →
    /// language to a <c>VS_FIXEDFILEINFO</c> whose signature is checked before any field is believed. An unreadable
    /// one is UNKNOWN, never a guessed number. Never throws.</summary>
    static string? ReadFileVersionResource(PEReader pe)
    {
        try
        {
            var dir = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
            if (dir.RelativeVirtualAddress == 0) return null;             // no resources at all — the common case for a lean DLL
            var block = pe.GetSectionData(dir.RelativeVirtualAddress);
            if (block.Length == 0) return null;
            // A bounded head of the section, not a .rsrc that may carry megabytes of icons; a hop past the bound yields null.
            var res = block.GetContent(0, Math.Min(block.Length, 64 * 1024));   // offsets inside the tree are relative to this base
            uint typeEntry = FindEntry(res, 0, 16);
            if ((typeEntry & 0x80000000u) == 0) return null;               // a type node's child is always a subdirectory
            uint nameEntry = FirstChild(res, (int)(typeEntry & 0x7FFFFFFF));
            if ((nameEntry & 0x80000000u) == 0) return null;
            uint leaf = FirstChild(res, (int)(nameEntry & 0x7FFFFFFF));
            if (leaf == 0 || (leaf & 0x80000000u) != 0) return null;       // a language node's child is the data entry
            int entry = (int)leaf;
            if (entry + 8 > res.Length) return null;
            int dataRva = ReadI32(res, entry);
            int dataSize = ReadI32(res, entry + 4);
            // The data entry addresses its bytes by RVA, not by an offset into the resource block.
            var data = pe.GetSectionData(dataRva);
            if (data.Length == 0) return null;
            var b = data.GetContent(0, Math.Min(dataSize, data.Length));

            // VS_VERSIONINFO: wLength, wValueLength, wType, then the UTF-16 key.
            const string Key = "VS_VERSION_INFO";
            int keyBytes = (Key.Length + 1) * 2;
            if (b.Length < 6 + keyBytes) return null;
            for (int i = 0; i < Key.Length; i++)
                if (b[6 + i * 2] != (byte)Key[i] || b[6 + i * 2 + 1] != 0) return null;
            int fixedAt = (6 + keyBytes + 3) & ~3;                        // VS_FIXEDFILEINFO is 4-byte aligned after the key
            if (fixedAt + 16 > b.Length) return null;
            if ((uint)ReadI32(b, fixedAt) != 0xFEEF04BDu) return null;    // not the struct the layout promised → no answer
            uint ms = (uint)ReadI32(b, fixedAt + 8), ls = (uint)ReadI32(b, fixedAt + 12);
            return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
        }
        catch { return null; /* corrupt or truncated resource tree → UNKNOWN, like every other failure here */ }
    }

    /// <summary>The entry value for a resource directory's child with the given id, or 0; its high bit marks a subdirectory.</summary>
    static uint FindEntry(System.Collections.Immutable.ImmutableArray<byte> res, int dirOff, int id)
    {
        if (dirOff + 16 > res.Length) return 0;
        int named = ReadU16(res, dirOff + 12), ids = ReadU16(res, dirOff + 14);
        for (int i = named; i < named + ids; i++)                         // id-keyed entries follow the name-keyed ones
        {
            int e = dirOff + 16 + i * 8;
            if (e + 8 > res.Length) return 0;
            if (ReadI32(res, e) == id) return (uint)ReadI32(res, e + 4);
        }
        return 0;
    }

    /// <summary>The first child entry of a resource directory, or 0 — a version resource carries exactly one.</summary>
    static uint FirstChild(System.Collections.Immutable.ImmutableArray<byte> res, int dirOff)
    {
        if (dirOff + 16 > res.Length) return 0;
        int count = ReadU16(res, dirOff + 12) + ReadU16(res, dirOff + 14);
        if (count == 0 || dirOff + 24 > res.Length) return 0;
        return (uint)ReadI32(res, dirOff + 16 + 4);
    }

    static int ReadI32(System.Collections.Immutable.ImmutableArray<byte> b, int off) =>
        b[off] | b[off + 1] << 8 | b[off + 2] << 16 | b[off + 3] << 24;

    static int ReadU16(System.Collections.Immutable.ImmutableArray<byte> b, int off) => b[off] | b[off + 1] << 8;

    /// <summary>Read a null-terminated ASCII string at an RVA, lower-cased and bounded; an unterminated run is corruption.</summary>
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

    /// <summary>Walk the PE export directory for name → export RVA: an EMPTY map for genuinely no export table,
    /// <c>null</c> for a present-but-corrupt one, and a declared RVA is read whatever the header's Size says. Never throws.</summary>
    static Dictionary<string, int>? ReadExportRvas(PEReader pe)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        // A zero Size beside a declared RVA is NOT "no exports" — reading it as empty would misclassify a real plugin NotSkse.
        if (dir.RelativeVirtualAddress == 0) return byName;                    // no export table → empty (genuinely no exports)
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

            // Bound the counts BEFORE allocating or looping: a corruption-controlled length is never trusted.
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

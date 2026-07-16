using System.Reflection.PortableExecutable;
using System.Text;

namespace HousecarlCore;

/// <summary>
/// Tier A+C of the SKSE-plugin-layer visibility gap (bug report 2026-06-08_gap_skse-plugin-layer-visibility):
/// reads what an SKSE plugin DLL DECLARES about itself, STATICALLY — no loading, no execution, no runtime state.
/// The whole capability's hard ceiling is tier E (DLL *behavior*), which stays UNREACHABLE by design; this reader
/// stops at the declared manifest, exactly the trustworthy line.
///
/// The mechanism, confirmed empirically against the live load order (297 real DLLs) and anchored to the two
/// CommonLib lineages' identical <c>SKSE::PluginVersionData</c> layout (alandtse/CommonLibVR@ng and
/// powerof3/CommonLibSSE@dev):
///
///   • The AE-era SKSE loader reads an exported DATA BLOB named <c>SKSEPlugin_Version</c> without executing the
///     DLL (plugin-skeleton.md §1), so its bytes are a static manifest: plugin name, author, version, the
///     version-independence flags (Address Library / signature-scanning / struct-compat), the compatible-runtime
///     list, and the XSE version floor. That is the whole of tier C, and it is what a modern plugin exports.
///   • The older SE/VR loader instead CALLS an exported FUNCTION <c>SKSEPlugin_Query</c> that fills its info at
///     runtime — so a query-ONLY (no <c>SKSEPlugin_Version</c>) plugin's metadata is NOT statically readable. We
///     classify it <see cref="SksePluginKind.LegacyQuery"/> and say so (Q3: an honest "can't read this
///     statically", never an invented name).
///   • A DLL in SKSE\Plugins with NONE of the SKSE exports is a bundled dependency, not a plugin
///     (<see cref="SksePluginKind.NotSkse"/>) — e.g. CrashLogger's msdia140.dll.
///
/// The struct layout is load-bearing and was VALIDATED byte-for-byte (SPID → "Spell Perk Item Distributor" /
/// "powerofthree", flags 0x5 = AddressLibrary|UpdatedStructs; OAR → AddressLibrary|NoStructs, compat 1.6.1170) —
/// the one non-obvious detail is <c>supportEmail[252]</c> (NOT 256), which puts <c>versionIndependenceEx</c> at
/// 0x304, not 0x308. See <see cref="DecodeVersionBlob"/> for the offset map. This reader NEVER guesses a layout:
/// the offsets come from the pinned headers, and the skse-reader-guard probe pins the decode against them.
/// </summary>
public static class SksePluginReader
{
    /// <summary>How a DLL under SKSE\Plugins relates to the SKSE plugin ABI. Drives what metadata (if any) is readable.</summary>
    public enum SksePluginKind
    {
        /// <summary>Exports the <c>SKSEPlugin_Version</c> data blob → full tier-C metadata is statically readable.</summary>
        Modern,
        /// <summary>Exports <c>SKSEPlugin_Query</c>/<c>SKSEPlugin_Load</c> but NO version blob — an SE/VR-era plugin whose
        /// metadata is filled at runtime, so it is not statically readable (Q3 honest degrade).</summary>
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
        /// the exact runtimes it lists — the compat-risk signal tier C exists to surface.</summary>
        public bool VersionIndependent => UsesAddressLibrary || UsesSignatureScanning;
    }

    /// <summary>One DLL's static SKSE identity. <see cref="Version"/> is non-null only for <see cref="SksePluginKind.Modern"/>.
    /// <see cref="Is64Bit"/> is <c>null</c> when the COFF machine field was never read (a non-PE / unopenable file whose
    /// bitness is genuinely UNKNOWN — it is NEVER presented as 32-bit on a guess). <see cref="Note"/> carries the Q3 reason
    /// for any non-Modern kind (why the metadata isn't readable).</summary>
    public sealed record SksePluginInfo(
        string FileName,
        SksePluginKind Kind,
        bool? Is64Bit,
        SkseVersionInfo? Version,
        string? Note);

    /// <summary>Read one DLL's static SKSE manifest off disk. Never throws for a bad/odd file — an unreadable image is
    /// reported as <see cref="SksePluginKind.Unreadable"/> with the reason (Q3). Reads the file with a plain read-share
    /// stream (no handle held at rest — the file is closed before return), consistent with houseCARL's "MO2/xEdit can
    /// move plugins freely" invariant.</summary>
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

    /// <summary>Read a DLL's static SKSE manifest from in-memory bytes — the BSA-packed twin of <see cref="Read"/>
    /// (the native-pairing audit PE-screens archive-shipped DLLs so a packed non-SKSE dependency never counts as an
    /// implementation candidate). Same never-throws contract.</summary>
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

        var exports = ReadExportRvas(pe);
        if (exports is null)   // export directory present but CORRUPT — a parse failure, NOT "no exports" (Q3: don't misclassify a corrupt DLL as a bundled dependency)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null, "corrupt PE export directory — could not enumerate exports");

        bool hasVersion = exports.TryGetValue("SKSEPlugin_Version", out int versionRva) && versionRva != 0;
        bool hasQuery = exports.ContainsKey("SKSEPlugin_Query");
        bool hasLoad = exports.ContainsKey("SKSEPlugin_Load") || exports.ContainsKey("SKSEPlugin_Preload");

        if (!hasVersion && !hasQuery && !hasLoad)
            return new SksePluginInfo(file, SksePluginKind.NotSkse, is64, null,
                "no SKSE export (SKSEPlugin_Version/Query/Load) — a bundled dependency DLL, not a plugin");

        if (!hasVersion)
            return new SksePluginInfo(file, SksePluginKind.LegacyQuery, is64, null,
                "legacy SE/VR plugin: exports SKSEPlugin_Query (metadata is filled at runtime), so name/version are not statically readable");

        // Modern: slice the version blob out of its section and decode. A real SKSEPluginVersionData is a FULL
        // 0x350-byte struct whose dataVersion (0x000) is kVersion (>= 1); a version export whose RVA maps to no
        // section, a forwarder, or a corrupt EAT yields a short or all-zero blob — degrade honestly rather than
        // present a phantom "" v0.0.0 plugin (Q3).
        var block = pe.GetSectionData(versionRva);
        byte[] blob = block.GetReader().ReadBytes(Math.Min(0x350, block.Length));
        if (blob.Length < 0x350 || BitConverter.ToUInt32(blob, 0) == 0)
            return new SksePluginInfo(file, SksePluginKind.Unreadable, is64, null,
                "exports SKSEPlugin_Version but its RVA does not resolve to a readable version blob (a forwarded or corrupt export)");
        var ver = DecodeVersionBlob(blob);
        return new SksePluginInfo(file, SksePluginKind.Modern, is64, ver, null);
    }

    /// <summary>Decode the raw <c>SKSEPlugin_Version</c> blob bytes into the manifest. PURE + bounds-checked so the CI
    /// probe can pin the exact offset map WITHOUT a real PE. The layout (identical in alandtse/CommonLibVR@ng and
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
    /// the exe resource 4). Pure; pinned by the native-pairing guard.</summary>
    public static bool RuntimeCompatible(SkseVersionInfo v, string installedRuntime)
        => v.VersionIndependent || v.CompatibleVersions.Any(cv => VersionsEqual(cv, installedRuntime));

    /// <summary>True when the dotted runtime version is AE-era (1.6 or later). Load-bearing for LegacyQuery
    /// adjudication: the AE SKSE loader loads ONLY plugins exporting the <c>SKSEPlugin_Version</c> data blob (the
    /// type doc's first bullet), so a query-only SE/VR-era plugin will NOT load on an AE runtime. A non-numeric or
    /// short version returns FALSE — unknown never becomes a "won't load" claim (Q3).</summary>
    public static bool IsAeRuntime(string runtime)
    {
        var seg = runtime.Split('.');
        if (seg.Length < 2 || !int.TryParse(seg[0].Trim(), out var maj) || !int.TryParse(seg[1].Trim(), out var min))
            return false;
        return maj > 1 || (maj == 1 && min >= 6);
    }

    /// <summary>Numeric dotted-version equality with zero-padding ("1.6.1170" == "1.6.1170.0"). A non-numeric
    /// segment ⇒ NOT equal (never guessed equal — a garbage compat entry must not accidentally PASS a lock, Q3).</summary>
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
            sb.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');   // printable ASCII only; others → space (Q3: no control chars leak into output)
        }
        return sb.ToString().Trim();
    }

    /// <summary>Walk the PE export directory and return name → export RVA (data exports point AT the data). Minimal by
    /// design: the SKSE loader itself resolves symbols by exact unmangled name string, so a name lookup is all we need.
    /// Returns an EMPTY map for a DLL with genuinely no export table (→ classify NotSkse), but <c>null</c> when the directory
    /// is present yet CORRUPT (a parse failure — the caller classifies Unreadable, never silently as a bundled dependency,
    /// Q3). Never throws.</summary>
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
        catch { return null; /* corrupt directory (bad RVA / truncated table / unterminated string) → parse-failure signal, NOT a silent empty map that would misclassify as NotSkse (Q3) */ }
        return byName;
    }
}

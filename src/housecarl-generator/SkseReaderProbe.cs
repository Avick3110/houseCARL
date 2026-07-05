using System.Text;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SKSE-plugin reader guard (gap 2026-06-08 — SKSE-plugin-layer visibility, tier C). Pins the two things
/// <see cref="SksePluginReader"/> got RIGHT only by reverse-engineering + empirical validation against the live load
/// order, so a later "cleanup" can't silently rot them:
///
///   • The <c>SKSEPlugin_Version</c> blob OFFSET MAP — most importantly that <c>supportEmail</c> is 252 bytes (NOT
///     256), which puts <c>versionIndependenceEx</c> at 0x304 and <c>versionIndependence</c> at 0x308. Arms A/F fail
///     the instant someone "fixes" email to 256 (both flag fields shift and the wrong bytes are read).
///   • The flag decode (Address Library / signature-scanning / updated-structs / no-structs), the version-locked vs
///     version-independent classification, the zero-terminated compatibleVersions list, and the REL::Version packing.
///   • The Read() path on a REAL PE image (the runner's own managed assembly) classifies as NotSkse WITHOUT throwing,
///     and a non-PE / missing file degrades to Unreadable (Q3 — never a throw that aborts an inventory).
///
/// The whole-inventory PE-walk over real DLLs is validated empirically against the actual load order (297 DLLs at
/// authoring time); this guard is the CI-runnable regression net for the decode contract + the honest-degrade paths.
/// Self-contained: synthetic blobs in memory + one temp junk file; no MO2 instance, no game data, no corpus.
/// </summary>
internal static class SkseReaderProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" skse-reader guard — SKSEPlugin_Version decode + honest-degrade (tier C)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- A: Address Library plugin (the OAR shape) — pins name/author/email at 0x008/0x108/0x208, the flags at
        //         0x304 (NoStructs) + 0x308 (AddressLibrary), compat, and the 3.0.0 version. ----
        Console.WriteLine("--- A: Address Library plugin (name/author/email + AddrLib+NoStructs + compat + version) ---");
        var a = SksePluginReader.DecodeVersionBlob(MakeBlob(
            pluginVersion: Pack(3, 0, 0, 0), name: "Test Plugin", author: "Tester", email: "t@example.com",
            viEx: 0x1 /*NoStructs*/, vi: 0x1 /*AddressLibrary*/, compat: new[] { Pack(1, 6, 1170, 0) }, xseMin: 0));
        Check(a.Name == "Test Plugin", $"Name == 'Test Plugin' (got '{a.Name}')");
        Check(a.Author == "Tester", $"Author == 'Tester' (got '{a.Author}')");
        Check(a.SupportEmail == "t@example.com", $"SupportEmail decoded (got '{a.SupportEmail}')");
        Check(a.PluginVersion == "3.0.0", $"PluginVersion == '3.0.0' (got '{a.PluginVersion}')");
        Check(a.UsesAddressLibrary, "UsesAddressLibrary true (vi bit0 @ 0x308)");
        Check(a.DeclaresNoStructs, "DeclaresNoStructs true (viEx bit0 @ 0x304 — proves email is 252, not 256)");
        Check(a.VersionIndependent, "VersionIndependent true (Address Library ⇒ not runtime-locked)");
        Check(a.CompatibleVersions.Count == 1 && a.CompatibleVersions[0] == "1.6.1170", "CompatibleVersions == [1.6.1170]");
        Check(a.MinimumXseVersion is null, "MinimumXseVersion null when xseMinimum == 0");

        // ---- B: version-LOCKED plugin (the fiss shape) — no independence flag ⇒ loads ONLY on its listed runtime. ----
        Console.WriteLine("\n--- B: version-LOCKED plugin (no independence flag → hard runtime list) ---");
        var b = SksePluginReader.DecodeVersionBlob(MakeBlob(
            pluginVersion: Pack(0, 0, 8, 13), name: "Locked", author: "", email: "",
            viEx: 0, vi: 0, compat: new[] { Pack(1, 6, 640, 0) }, xseMin: 0));
        Check(!b.VersionIndependent, "VersionIndependent false (no AddrLib / no SigScan)");
        Check(!b.UsesAddressLibrary && !b.UsesSignatureScanning, "no independence flags set");
        Check(b.CompatibleVersions.Count == 1 && b.CompatibleVersions[0] == "1.6.640", "CompatibleVersions == [1.6.640] (the hard target)");
        Check(b.PluginVersion == "0.0.8.13", $"PluginVersion keeps a non-zero build (got '{b.PluginVersion}')");

        // ---- C: signature-scanning + updated-structs + an XSE floor. ----
        Console.WriteLine("\n--- C: signature-scanning + updated-structs + XSE minimum ---");
        var c = SksePluginReader.DecodeVersionBlob(MakeBlob(
            pluginVersion: Pack(1, 2, 3, 0), name: "Sig", author: "", email: "",
            viEx: 0, vi: 0x2 | 0x4 /*Signatures | StructsPost629*/, compat: Array.Empty<uint>(), xseMin: Pack(2, 2, 3, 0)));
        Check(c.UsesSignatureScanning, "UsesSignatureScanning true (vi bit1)");
        Check(c.UsesUpdatedStructs, "UsesUpdatedStructs true (vi bit2)");
        Check(!c.UsesAddressLibrary, "UsesAddressLibrary false");
        Check(c.VersionIndependent, "VersionIndependent true (signature scanning also frees it from the runtime list)");
        Check(c.MinimumXseVersion == "2.2.3", $"MinimumXseVersion == '2.2.3' (got '{c.MinimumXseVersion ?? "null"}')");

        // ---- D: compatibleVersions is ZERO-TERMINATED — a value after a 0 is ignored (loader semantics). ----
        Console.WriteLine("\n--- D: compatibleVersions zero-termination ---");
        var d = SksePluginReader.DecodeVersionBlob(MakeBlob(
            pluginVersion: 0, name: "Z", author: "", email: "",
            viEx: 0, vi: 0, compat: new[] { Pack(1, 5, 97, 0), Pack(1, 6, 640, 0), 0u, Pack(9, 9, 9, 0) }, xseMin: 0));
        Check(d.CompatibleVersions.Count == 2, $"stops at the zero terminator — 2 entries, not 4 (got {d.CompatibleVersions.Count})");
        Check(d.CompatibleVersions.SequenceEqual(new[] { "1.5.97", "1.6.640" }), "the two pre-terminator versions decode");

        // ---- E: REL::Version packing is exact (maj<<24 | min<<16 | patch<<4 | build). ----
        Console.WriteLine("\n--- E: REL::Version unpack ---");
        Check(SksePluginReader.UnpackVersion(0x07000000) == "7.0.0", "0x07000000 → 7.0.0 (SPID major-only)");
        Check(SksePluginReader.UnpackVersion(0x01064920) == "1.6.1170", "0x01064920 → 1.6.1170 (RUNTIME_SSE_LATEST)");
        Check(SksePluginReader.UnpackVersion(0x0000008D) == "0.0.8.13", "0x0000008D → 0.0.8.13 (build nibble preserved)");

        // ---- F: the 252-byte offset regression, isolated — swap the two flag fields and prove each is read at its own
        //         offset. If email were 256, both would shift and BOTH sub-checks would flip. ----
        Console.WriteLine("\n--- F: supportEmail-is-252 offset regression (viEx@0x304 vs vi@0x308 are distinct) ---");
        var f1 = SksePluginReader.DecodeVersionBlob(MakeBlob(0, "F1", "", "", viEx: 0x1, vi: 0x0, compat: Array.Empty<uint>(), xseMin: 0));
        Check(f1.DeclaresNoStructs && !f1.UsesAddressLibrary, "viEx=1,vi=0 → NoStructs only (0x304 is viEx)");
        var f2 = SksePluginReader.DecodeVersionBlob(MakeBlob(0, "F2", "", "", viEx: 0x0, vi: 0x1, compat: Array.Empty<uint>(), xseMin: 0));
        Check(!f2.DeclaresNoStructs && f2.UsesAddressLibrary, "viEx=0,vi=1 → AddressLibrary only (0x308 is vi)");

        // ---- G: Read() on a REAL PE (the runner's own managed assembly) — classifies without throwing. ----
        Console.WriteLine("\n--- G: Read() on a real PE image (managed assembly → NotSkse, no throw) ---");
        string ownPath = typeof(SksePluginReader).Assembly.Location;
        var g = SksePluginReader.Read(ownPath);
        Check(g.Kind == SksePluginReader.SksePluginKind.NotSkse,
              $"a managed assembly with no SKSE export classifies NotSkse (got {g.Kind})");
        Check(g.Note is { Length: > 0 }, "NotSkse carries a Q3 note explaining why");
        Check(g.Version is null, "no version manifest for a non-plugin DLL");

        // ---- H: honest degrade — a non-PE file and a missing path both yield Unreadable, never a throw. ----
        Console.WriteLine("\n--- H: honest-degrade (non-PE + missing path → Unreadable, no throw) ---");
        var junk = Path.Combine(Path.GetTempPath(), "hc-skse-junk-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("this is not a PE file at all, just some bytes"));
            var h1 = SksePluginReader.Read(junk);
            Check(h1.Kind == SksePluginReader.SksePluginKind.Unreadable, $"non-PE bytes → Unreadable (got {h1.Kind})");
            var h2 = SksePluginReader.Read(Path.Combine(Path.GetTempPath(), "hc-skse-does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll"));
            Check(h2.Kind == SksePluginReader.SksePluginKind.Unreadable, $"missing file → Unreadable, no throw (got {h2.Kind})");
        }
        finally { try { File.Delete(junk); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Lay out a synthetic <c>SKSEPlugin_Version</c> blob at the CONFIRMED offsets — the reference layout the
    /// reader must agree with. (This is the "correct answer key"; the reader is what's under test.)</summary>
    static byte[] MakeBlob(uint pluginVersion, string name, string author, string email, uint viEx, uint vi, uint[] compat, uint xseMin)
    {
        var b = new byte[0x350];
        BitConverter.GetBytes(1u).CopyTo(b, 0x000);              // dataVersion = kVersion
        BitConverter.GetBytes(pluginVersion).CopyTo(b, 0x004);
        WriteAscii(b, 0x008, name, 256);
        WriteAscii(b, 0x108, author, 256);
        WriteAscii(b, 0x208, email, 252);                        // 252 — the load-bearing size
        BitConverter.GetBytes(viEx).CopyTo(b, 0x304);
        BitConverter.GetBytes(vi).CopyTo(b, 0x308);
        for (int i = 0; i < compat.Length && i < 16; i++) BitConverter.GetBytes(compat[i]).CopyTo(b, 0x30C + i * 4);
        BitConverter.GetBytes(xseMin).CopyTo(b, 0x34C);
        return b;
    }

    static void WriteAscii(byte[] b, int off, string s, int max)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, max - 1));   // leave the null terminator (rest is already zero)
    }

    /// <summary>Mirror of REL::Version::pack (maj 8b &lt;&lt; 24 | min 8b &lt;&lt; 16 | patch 12b &lt;&lt; 4 | build 4b) — for building
    /// the answer-key blobs. The reader's UnpackVersion is the inverse under test.</summary>
    static uint Pack(int maj, int min, int patch, int build) =>
        (uint)(((maj & 0xFF) << 24) | ((min & 0xFF) << 16) | ((patch & 0xFFF) << 4) | (build & 0xF));
}

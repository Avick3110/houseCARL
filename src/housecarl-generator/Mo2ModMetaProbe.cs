using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Mo2ModMeta parser guard (Nexus Tier 0 PR review fold). The meta.ini Nexus-update-cache reader is pure, offline, and
/// deterministic with the fiddliest logic in the capability PR — the QSettings quirks (@ByteArray/@Invalid/quotes/doubled
/// backslash) plus the exact-key match that keeps [General]'s modid from being shadowed by [installedFiles]'s 1\modid —
/// so it earns a cheap CI guard. Writes synthetic meta.ini files to temp and asserts every field + quirk. Self-contained;
/// no game data, no corpus.
/// </summary>
internal static class Mo2ModMetaProbe
{
    [CiProbe("mo2-modmeta-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" mo2-modmeta guard — meta.ini Nexus update-cache parse");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var dir = Path.Combine(Path.GetTempPath(), "hc-modmeta-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A — a standard Nexus meta.ini: all fields, whitespace around '=', and an [installedFiles] section AFTER
            //     [General] whose 1\modid must NOT be read as the mod's modid (exact-key match).
            var a = Write(dir, "a.ini",
                "[General]",
                "gameName=SkyrimSE",
                "modid=12604",
                "version = 6.9",
                "newestVersion=6.11",
                "ignoredVersion=6.10",
                "lastNexusUpdate=1778020881",
                "[installedFiles]",
                "1\\modid=99999",
                "1\\fileid=749043");
            var ma = Mo2ModMeta.Read(a);
            Check(ma is not null, "A: standard meta.ini reads");
            Check(ma!.ModId == 12604, "A: modid = 12604 (NOT the [installedFiles] 99999)");
            Check(ma.Version == "6.9", "A: version tolerates whitespace-around-= → '6.9'");
            Check(ma.NewestVersion == "6.11", "A: newestVersion = 6.11");
            Check(ma.IgnoredVersion == "6.10", "A: ignoredVersion = 6.10");
            Check(ma.LastNexusUpdate == "1778020881", "A: lastNexusUpdate raw unix seconds");
            Check(Seq(ma.InstalledFileIds, 749043), "A: [installedFiles] 1\\fileid → [749043] (NOT the 1\\modid 99999)");

            // B — QSettings quirks: @ByteArray wrap, @Invalid unset, surrounding quotes, doubled backslash.
            var b = Write(dir, "b.ini",
                "[General]",
                "modid=@ByteArray(266)",
                "version=\"5.2SE\"",
                "newestVersion=@Invalid()",
                "ignoredVersion=a\\\\b");          // literal a\\b in the file → a\b after unescape
            var mb = Mo2ModMeta.Read(b);
            Check(mb!.ModId == 266, "B: @ByteArray(266) → 266");
            Check(mb.Version == "5.2SE", "B: surrounding quotes stripped → 5.2SE");
            Check(mb.NewestVersion is null, "B: @Invalid() → null");
            Check(mb.IgnoredVersion == "a\\b", "B: doubled backslash unescaped → a\\b");
            Check(mb.InstalledFileIds.Count == 0, "B: no [installedFiles] section → empty fileids (never null)");

            // C — no/invalid modid → ModId 0; absent fields → null (still returns a record, never throws).
            var c = Write(dir, "c.ini", "[General]", "version=1.0", "modid=notanumber");
            var mc = Mo2ModMeta.Read(c);
            Check(mc is not null && mc.ModId == 0, "C: non-integer modid → 0");
            Check(mc!.NewestVersion is null && mc.IgnoredVersion is null && mc.LastNexusUpdate is null, "C: absent fields → null");
            Check(mc.InstalledFileIds.Count == 0, "C: no [installedFiles] → empty fileids");

            // E — MULTIPLE installed files, with the size= key BETWEEN the indexed keys and the indices OUT OF ORDER
            //     (real meta.ini writes them in any order — AMON's live file even puts size= between 1\modid and 1\fileid).
            //     Must return every fileid, ordered by index N, ignoring the size= and N\modid siblings.
            var e = Write(dir, "e.ini",
                "[General]",
                "modid=126608",
                "[installedFiles]",
                "2\\fileid=222",
                "1\\modid=126608",
                "size=2",
                "2\\modid=126608",
                "1\\fileid=111");
            var me = Mo2ModMeta.Read(e);
            Check(Seq(me!.InstalledFileIds, 111, 222), "E: multi fileid, size= interleaved, out-of-order N → [111,222] by index");

            // F — a FOMOD/manual install: [installedFiles] size=0 with NO fileid (City Trees is the live example). Must be
            //     empty (⇒ the loud no-fileid fallback), never a guess. Also proves a section AFTER installedFiles doesn't leak.
            var f = Write(dir, "f.ini",
                "[General]",
                "modid=35546",
                "[installedFiles]",
                "size=0",
                "[Plugins]",
                "1\\fileid=999");                 // a fileid OUTSIDE [installedFiles] must be ignored (scoped scan)
            var mf = Mo2ModMeta.Read(f);
            Check(mf!.InstalledFileIds.Count == 0, "F: [installedFiles] size=0 → empty fileids (FOMOD/manual); stray fileid in [Plugins] ignored");

            // D — a file that doesn't exist → null, never a throw (the caller keeps walking the mods folder).
            Check(Mo2ModMeta.Read(Path.Combine(dir, "does-not-exist.ini")) is null, "D: missing file → null, no throw");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine(fail == 0 ? "[mo2-modmeta-guard] PASS — meta.ini parse holds." : $"[mo2-modmeta-guard] FAIL ({fail})");
        return fail;
    }

    static string Write(string dir, string name, params string[] lines)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllLines(p, lines);
        return p;
    }

    /// <summary>True iff the actual fileid list equals the expected ints in order.</summary>
    static bool Seq(IReadOnlyList<int> actual, params int[] expected)
    {
        if (actual.Count != expected.Length) return false;
        for (int i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) return false;
        return true;
    }
}

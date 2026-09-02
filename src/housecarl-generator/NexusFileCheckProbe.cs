using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// FILE-LEVEL Nexus update-check guard (the fix for the shipped multi-file-page false positive: a mod-level
/// "installed == newest MAIN" compare is confidently WRONG when a page hosts many independently-versioned files).
/// Locks the three pure pieces of the fix — none touch the network:
///   • NexusClient.ComputeStatus — the currency verdict from an installed fileid + the mod's file list: a live-category
///     file ⇒ Current, an OLD_VERSION/ARCHIVED file ⇒ Outdated (pointing to the newest SAME-NAME live file, not the
///     page's global newest), an absent fileid ⇒ FileGone (loud), no fileid ⇒ NoFileId/LatestOnly (loud fallback,
///     never a confident mod-level verdict), plus multi-file aggregation and the not-found state. A mod hidden from the
///     mods() SEARCH but resolvable via its direct modFiles lookup (the manager-only/nxm class) is still checked from
///     its files, never mis-stamped NotFound — the search's "found" flag no longer vetoes already-fetched data (G/J).
///   • NexusTools.ParseUpdatePairs — the 'id#fileid' input grammar (file-level vs version vs bare, junk surfaced).
///   • NexusClient.GroupRequests — same-modId-across-folders MERGES fileids (the Xtudo multi-folder case), never
///     dedup-drops a folder.
/// Synthetic data mirrors the real AMON ENB (99786) page Aaron caught. Self-contained; no game data, no network.
/// </summary>
internal static class NexusFileCheckProbe
{
    [CiProbe("nexus-file-check-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nexus-file-check guard — file-level currency verdicts + id#fileid parse + modId grouping");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // Synthetic file list mirroring AMON ENB (99786): a MULTI-MAIN page — the exact shape that produced the shipped
        // false positive. 585300 is the LIVE MAIN "Esp Fix v2" Aaron actually installed; 775265 is the v10 preset the old
        // mod-level compare wrongly measured him against; 585294/483794 are ARCHIVED "Esp Fix" copies.
        var files = new List<(int fileId, string name, string? version, string category, long date)>
        {
            (585300, "Amon NAT III Esp Fix",       "2",  "MAIN",     1737000000L),
            (775265, "AMON ENB For NAT III 2026",   "10", "MAIN",     1751000000L),
            (474157, "Fix for Sub Surface Scattering", "1", "MAIN",   1600000000L),
            (585294, "Amon NAT III Esp Fix",       "2",  "ARCHIVED", 1736000000L),
            (483794, "Amon NAT III Esp Fix",       "1",  "ARCHIVED", 1700000000L),
        };

        // A — THE false-positive kill: installed 585300 (a LIVE MAIN) ⇒ Current, NOT "behind, latest v10".
        var a = NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "2.0.0.0", new[] { 585300 }, files);
        Check(a.Verdict == UpdateVerdict.Current, "A: installed file in a LIVE category → Current (NOT compared to the v10 preset)");
        Check(a.Files.Count == 1 && a.Files[0].Verdict == FileVerdict.Live, "A: the installed file is reported Live");

        // B — an installed file the author RETIRED ⇒ Outdated, pointing to the newest SAME-NAME live file (585300 v2),
        //     never the page's global newest (the v10 preset is a different variant line).
        var b = NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "1", new[] { 483794 }, files);
        Check(b.Verdict == UpdateVerdict.Outdated, "B: installed file in ARCHIVED → Outdated");
        Check(b.Files.Count == 1 && b.Files[0].Verdict == FileVerdict.Superseded, "B: the installed file is reported Superseded");
        Check(b.Files[0].NewestSameName == "Amon NAT III Esp Fix" && b.Files[0].NewestSameVersion == "2",
              "B: points to the newest SAME-NAME live file (Esp Fix v2), not the v10 preset");

        // C — an installed fileid no longer on the page ⇒ FileGone (loud unknown), never silently "current".
        var c = NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", null, new[] { 999999 }, files);
        Check(c.Verdict == UpdateVerdict.FileGone, "C: installed fileid absent from the file list → FileGone (loud)");
        Check(c.Files.Count == 1 && c.Files[0].Verdict == FileVerdict.Missing, "C: the installed file is reported Missing");

        // D — MULTIPLE installed files (the Xtudo case), one live + one retired ⇒ Outdated headline, both detailed.
        var d = NexusClient.ComputeStatus(99786, true, "AMON ENB", null, null, new[] { 585300, 483794 }, files);
        Check(d.Verdict == UpdateVerdict.Outdated, "D: multi-file, ≥1 retired → Outdated headline");
        Check(d.Files.Count == 2, "D: both installed files detailed");

        // D2 — MULTIPLE installed files, all live ⇒ Current.
        var d2 = NexusClient.ComputeStatus(99786, true, "AMON ENB", null, null, new[] { 585300, 775265 }, files);
        Check(d2.Verdict == UpdateVerdict.Current, "D2: multi-file, all live → Current");

        // E — NO fileid but a version, on a MULTI-main page ⇒ NoFileId (loud); LiveMainCount surfaces the ambiguity.
        var e = NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "2.0.0.0", Array.Empty<int>(), files);
        Check(e.Verdict == UpdateVerdict.NoFileId, "E: no fileid + version → NoFileId (loud, never a confident mod-level verdict)");
        Check(e.LiveMainCount == 3, "E: LiveMainCount counts every live MAIN (3) — the multi-main ambiguity signal");

        // F — bare id (no version, no fileid) ⇒ LatestOnly.
        var f = NexusClient.ComputeStatus(3863, true, "Some Mod", "1.0", null, Array.Empty<int>(), files);
        Check(f.Verdict == UpdateVerdict.LatestOnly, "F: bare id (no version, no fileid) → LatestOnly");

        // G — GENUINELY not found: absent from the mods() search AND the modFiles lookup returned NOTHING (a missing mod
        //     comes back as an empty file list, never an error). The EMPTY list is what makes it genuine — contrast J,
        //     where files DID come back for a search-absent mod and it must still be checked.
        var g = NexusClient.ComputeStatus(1, false, null, null, "1.0", new[] { 123 }, new List<(int, string, string?, string, long)>());
        Check(g.Verdict == UpdateVerdict.NotFound, "G: not in search AND no files returned → NotFound (genuinely gone/wrong-id)");

        // J — the nxm-only BLIND SPOT this guard was extended to lock. Nexus EXCLUDES manager-only (direct-download-
        //     disabled) mods from the mods() search collection, so found=false — but their direct modFiles lookup resolves
        //     fine and returned files. The installed file MUST be checked from that list, never stamped NotFound (a
        //     confidently-wrong "not found" for a real, checkable mod — the same false-answer class the file-level fix
        //     exists to kill). 585300 is a LIVE MAIN in the synthetic list ⇒ Current, and the mod reports Found (exists).
        var jLive = NexusClient.ComputeStatus(90696, false, null, null, null, new[] { 585300 }, files);
        Check(jLive.Verdict == UpdateVerdict.Current, "J: found=false but modFiles returned files → check them (Current), NOT NotFound (nxm-only)");
        Check(jLive.Found, "J: a mod resolved via its file list is reported Found — it exists (a null friendly name is fine)");

        // J2 — same blind spot, installed file RETIRED ⇒ Outdated (still checked from the file list, not NotFound).
        var jOut = NexusClient.ComputeStatus(132337, false, null, null, "1", new[] { 483794 }, files);
        Check(jOut.Verdict == UpdateVerdict.Outdated, "J2: found=false + retired installed file → Outdated (checked, not NotFound)");

        // J3 — a search-absent mod with NO fileid (FOMOD/manual) can't be file-checked, but we now know it EXISTS (files
        //      came back), so it degrades to the LOUD NoFileId fallback — never NotFound.
        var jNo = NexusClient.ComputeStatus(90696, false, null, null, "2.0.0.0", Array.Empty<int>(), files);
        Check(jNo.Verdict == UpdateVerdict.NoFileId, "J3: found=false, files present, no fileid → NoFileId fallback (exists), not NotFound");

        // J4 — the SAFETY-relevant nxm combination: a search-absent mod (found=false) whose modFiles DID come back, but the
        //      exact installed fileid is NO LONGER in that list (a hidden/pulled file) ⇒ FileGone (loud unknown), never
        //      NotFound and never a silent "current". 999999 isn't in the synthetic list.
        var jGone = NexusClient.ComputeStatus(90696, false, null, null, "1.0", new[] { 999999 }, files);
        Check(jGone.Verdict == UpdateVerdict.FileGone, "J4: found=false + files present + installed fileid absent → FileGone (loud), not NotFound");

        // H — single-main page, no fileid + version ⇒ NoFileId with LiveMainCount==1 (the labeled version-compare case).
        var single = new List<(int, string, string?, string, long)>
        {
            (10, "Solo", "3.1", "MAIN", 100L),
            (11, "Solo", "3.0", "OLD_VERSION", 90L),
        };
        var h = NexusClient.ComputeStatus(266, true, "Solo Mod", "3.0", "3.0", Array.Empty<int>(), single);
        Check(h.Verdict == UpdateVerdict.NoFileId && h.LiveMainCount == 1 && h.LatestMainVersion == "3.1",
              "H: no fileid, single live MAIN → NoFileId + LiveMainCount 1 + newest MAIN surfaced");

        // I — an UNKNOWN/new category on the installed file is treated as LIVE (superseded is the CLOSED OLD/ARCHIVED
        //     set), and the category is carried through so it's visible (Q3), never silently bucketed as retired.
        var unk = new List<(int, string, string?, string, long)> { (20, "New", "1", "PENDING_REVIEW", 100L) };
        var iRes = NexusClient.ComputeStatus(500, true, "New Cat", "1", null, new[] { 20 }, unk);
        Check(iRes.Verdict == UpdateVerdict.Current && iRes.Files[0].Verdict == FileVerdict.Live && iRes.Files[0].Category == "PENDING_REVIEW",
              "I: unknown category → treated Live, category carried through (not mis-retired)");

        // ---- id#fileid PARSE (ParseUpdatePairs) ----
        var (pairs, bad) = NexusTools.ParseUpdatePairs("99786#585300, 126608#533265#533266, 12604=6.9, 266 4.3.8a, 3863, 99786#, abc#1, junk");
        Check(pairs.Count == 5, "parse: 5 readable entries");
        Check(pairs.Any(p => p.modId == 99786 && p.installed is null && p.fileIds.Count == 1 && p.fileIds[0] == 585300),
              "parse: '99786#585300' → file-level [585300]");
        Check(pairs.Any(p => p.modId == 126608 && p.fileIds.Count == 2 && p.fileIds[0] == 533265 && p.fileIds[1] == 533266),
              "parse: '126608#533265#533266' → two fileids");
        Check(pairs.Any(p => p.modId == 12604 && p.installed == "6.9" && p.fileIds.Count == 0), "parse: '12604=6.9' → version, no fileid");
        Check(pairs.Any(p => p.modId == 266 && p.installed == "4.3.8a"), "parse: '266 4.3.8a' → space-separated version");
        Check(pairs.Any(p => p.modId == 3863 && p.installed is null && p.fileIds.Count == 0), "parse: bare '3863' → latest-only");
        Check(bad.Contains("99786#") && bad.Any(x => x.StartsWith("abc#")) && bad.Contains("junk"),
              "parse: '99786#' (no fileid), 'abc#1' (bad modid), 'junk' → surfaced as bad, not silently dropped");

        // ---- modId GROUPING (GroupRequests) — the multi-folder Xtudo case: same modid across folders MERGES, never drops ----
        var grouped = NexusClient.GroupRequests(new (int, string?, IReadOnlyList<int>)[]
        {
            (126608, "1.3", new[] { 533265 }),
            (126608, "1.1", new[] { 533266 }),
            (99786, null, new[] { 585300 }),
            (126608, null, new[] { 533265 }),   // duplicate fileid — must dedupe
        });
        Check(grouped.order.Count == 2 && grouped.order[0] == 126608 && grouped.order[1] == 99786, "group: 2 modIds, first-seen order");
        var g126 = grouped.map[126608];
        Check(g126.fileIds.Count == 2 && g126.fileIds.Contains(533265) && g126.fileIds.Contains(533266),
              "group: same modId across folders MERGES fileids (never dedup-drops a folder), dedupes repeats");
        Check(g126.installed == "1.3", "group: keeps the FIRST non-empty installed version");

        Console.WriteLine(fail == 0
            ? "[nexus-file-check] PASS — file-level verdicts + parse + grouping hold."
            : $"[nexus-file-check] FAIL ({fail})");
        return fail;
    }
}

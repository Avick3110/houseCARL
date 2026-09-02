using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COPY PARSER guard — <c>housecarl_copy</c>'s argument layer driven THROUGH THE TOOL METHOD, over a synthetic MO2
/// instance, so the WIRE spelling of every parameter is under test rather than the typed values the service already
/// has its own guards for.
///
/// <para><b>Why this guard exists as a separate lane.</b> Every claim in the tool's Description below the record
/// layer is a claim about a mapping — caller string → typed argument — and a mapping is exactly what a
/// service-level fixture cannot see: <c>copy-service-guard</c> hands <see cref="HousecarlCore.WalkExclusion"/>
/// values straight to <c>CopyClosure</c>, so deleting the <c>'Type:stop'</c> / <c>'Type:refuse'</c> parse would
/// leave it green while the documented spelling stopped working. That is #339's lesson stated one layer up: an arm
/// that drives the service does not test the wire.</para>
///
/// <para>Pins, each of them a sentence the Description promises:
///   EXCLUSIONS  — <c>'Type:refuse'</c> fails the whole copy, <c>'Type:stop'</c> prunes and keeps the link, and the
///                 two are told apart by RESULT, not by inspecting an argument. The severity token is
///                 case-insensitive; an unknown one refuses by name; an entry naming no type refuses by name.
///   DESTINATION — exactly one of <c>target=</c> / <c>new_editorid=</c>; both and neither refuse identically, and a
///                 whitespace-only <c>target=</c> counts as ABSENT rather than as a destination that then fails to
///                 parse (the two produce different sentences, and only one of them is actionable).
///   FORMIDS     — a bad <c>from=</c> or <c>target=</c> refuses naming the parameter AND the expected shape, and the
///                 <c>from=</c> refusal fires AHEAD of the destination gate, so a call that is wrong in two ways is
///                 told about the one it must fix first.
///   SEED PATHS  — <c>seed_paths=</c> is REQUIRED: absent, empty, and blank-only all refuse with the same sentence
///                 rather than running a walk that would copy nothing. Entries are trimmed.
///   SOURCE      — the documented default: no <c>from_source=</c> means <c>['winner']</c>, and the readback SAYS
///                 <c>winner</c>. An empty list takes the same default rather than reaching the service's
///                 empty-universe refusal.
/// </para>
///
/// <para>GUARD-NOTE (branch rule): every arm reads the tool's own STRING result, so no arm can throw under a
/// sabotage and hide the arms behind it. The <c>!</c>-operator sweep has nothing to remove here by construction.</para>
/// Run: dotnet run --project src/housecarl-generator -- copy-parser-guard
/// </summary>
public static class CopyParserProbe
{
    [CiProbe("copy-parser-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COPY PARSER guard (the wire, not the service)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }
        static bool Has(string text, params string[] needles) =>
            needles.All(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

        var root = Path.Combine(Path.GetTempPath(), "hc-copy-parser-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            var data = Path.Combine(inst, "game", "Data");
            var prof = Path.Combine(inst, "profiles", "Default");
            foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

            // The fixture is deliberately the SMALLEST one that can discriminate an exclusion severity: an NPC whose
            // HeadParts seed reaches a head part, which reaches a texture set. Both live in the source plugin, so both
            // are in scope; the NPC's own Race lives in the shared base master, so it is NOT in scope and the clone
            // lane's required-link refusal never fires. That leaves the TextureSet as the one record an exclusion can
            // change the fate of, which is what makes 'stop' and 'refuse' distinguishable by result alone.
            var baseKey = new ModKey("HcBase", ModType.Master);
            var raceFk = new FormKey(baseKey, 0x800);
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            baseMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcRace" });
            WriteModDirect(mods, "BaseMod", baseMod);

            var srcKey = new ModKey("Src", ModType.Plugin);
            var txstFk = new FormKey(srcKey, 0x800);
            var hpFk = new FormKey(srcKey, 0x801);
            var srcNpcFk = new FormKey(srcKey, 0x802);
            var srcMod = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
            srcMod.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
            var srcHp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
            srcHp.TextureSet.SetTo(txstFk);
            srcMod.HeadParts.Add(srcHp);
            var srcNpc = new Npc(srcNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
            srcNpc.Race.SetTo(raceFk);
            srcNpc.HeadParts.Add(hpFk);
            // Factions is left EMPTY on purpose. It is the shape-ruling arm's subject, and an empty list is the
            // case a content-based shape test gets wrong — so the fixture is built where the wrong answer passes.
            srcMod.Npcs.Add(srcNpc);
            WriteModDirect(mods, "SrcMod", srcMod, baseMod);

            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+SrcMod\r\n+BaseMod\r\n");
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\nSrc.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n*Src.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

            using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
            var from = srcNpcFk.ToString();
            var seed = new[] { "HeadParts" };

            // ---- 0. The baseline the exclusion arms are read against -----------------------------------------
            // Without exclusions BOTH source records are internalized and the artifact is standalone. Every
            // exclusion arm below is a departure from THIS text, so a fixture that silently stopped copying the
            // texture set would fail here first rather than making the 'stop' arm pass for the wrong reason.
            var baseline = CopyTools.Copy(svc, from, null, seed, null, null, "PBaseline", "PBaseline");
            Check(Has(baseline, "CLONED"), $"baseline: the fixture copies at all ({First(baseline)})");
            Check(Has(baseline, "SrcHair") && Has(baseline, "SrcTex"),
                "baseline: BOTH source records are internalized when nothing is excluded");
            Check(Has(baseline, "standalone: the source is NOT a master"),
                "baseline: and the artifact does not master the source");

            // ---- 1. exclude_types — the wire spelling reaching the walk's severities --------------------------
            var refuse = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:refuse" }, null, "PRefuse", "PRefuse");
            Check(Has(refuse, "error:") && Has(refuse, "marks 'refuse'") && Has(refuse, "TextureSet"),
                "'TextureSet:refuse' on the WIRE fails the whole copy, naming the type and the severity");
            // Needles from BOTH claims on purpose: "nothing was written" is the tail of every refusal this tool
            // gives, so an arm testing it alone stays green under a sabotage that swaps in a different refusal.
            Check(Has(refuse, "marks 'refuse'", "Nothing was written"), "…and THAT refusal says nothing was written");

            var stop = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:stop" }, null, "PStop", "PStop");
            Check(Has(stop, "CLONED"), $"'TextureSet:stop' on the WIRE lets the copy through ({First(stop)})");
            // "internalized under new FormIDs" is in the needles because without it this arm passes on the REFUSAL
            // text too — that names the head part while never mentioning the texture set, so both halves read true
            // on a result that copied nothing. An arm has to be able to fail in the direction it is claiming.
            Check(Has(stop, "internalized under new FormIDs", "SrcHair") && !stop.Contains("SrcTex", StringComparison.OrdinalIgnoreCase),
                "…having PRUNED the excluded record: the head part is internalized, the texture set is not");
            // The honest consequence of 'stop' on a record defined in the source: the kept link masters it, so the
            // artifact is NOT standalone and the alarm — not a quiet note — is what says so.
            Check(Has(stop, "the source IS among the masters", "NOT standalone"),
                "…and the kept link's cost is ALARMED, not implied: this artifact masters the source");

            var upper = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:REFUSE" }, null, "PUpper", "PUpper");
            Check(Has(upper, "error:") && Has(upper, "marks 'refuse'"), "the severity token is case-insensitive");

            var bare = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet" }, null, "PBare", "PBare");
            Check(Has(bare, "error:") && Has(bare, "marks 'refuse'"),
                "an entry with NO severity defaults to the loud one ('refuse'), never to the quiet one");

            var badSev = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:prune" }, null, "PBadSev", "PBadSev");
            Check(Has(badSev, "error:") && Has(badSev, "'prune'") && Has(badSev, "stop") && Has(badSev, "refuse"),
                "an unknown severity refuses by name, quoting what was typed and both legal spellings");

            var noType = CopyTools.Copy(svc, from, null, seed, new[] { ":refuse" }, null, "PNoType", "PNoType");
            Check(Has(noType, "error:") && Has(noType, "names no record type"),
                "an entry naming no record type refuses by name");

            // A blank exclusion REFUSES, matching the rule seed_paths and from_source already enforce. This arm
            // used to assert the opposite — that blanks are dropped — which is what pinned the bug in place: a
            // templating slip yielding one empty entry ran the copy with FEWER exclusions than the caller passed,
            // and nothing in the response said so. A blank has no index to shift, so it refuses by CONTENT.
            var blankExcl = CopyTools.Copy(svc, from, null, seed, new[] { "", "   " }, null, "PBlankX", "PBlankX");
            Check(Has(blankExcl, "error:") && Has(blankExcl, "blank entry"),
                "a list of only blank exclusion entries REFUSES rather than excluding nothing");
            Check(!Has(blankExcl, "CLONED"), "…and writes nothing");

            // The case that actually bites: a REAL exclusion beside a blank. Dropping the blank silently applied
            // one fewer exclusion than the caller wrote, which is the 'Race:refuse' that never fired.
            var blankBeside = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:refuse", "  " }, null, "PBlankY", "PBlankY");
            Check(Has(blankBeside, "error:") && Has(blankBeside, "blank entry"),
                "…and a blank BESIDE a real exclusion refuses too, rather than quietly applying one fewer");

            var dupExcl = CopyTools.Copy(svc, from, null, seed, new[] { "TextureSet:stop", "texture" + "set:refuse" }, null, "PDup", "PDup");
            Check(Has(dupExcl, "error:") && Has(dupExcl, "more than once") && !Has(dupExcl, "failed unexpectedly"),
                "a DUPLICATE exclude_types type refuses by name — it used to throw and be reported as 'not bad input'");

            // ---- 1b. the seed-shape boundary (shape ruling (a)) ------------------------------------------------
            // Factions is link-BEARING but its entries are structs, not links. It used to seed zero silently and,
            // in the attach lane, empty the target's list while reporting a successful attach.
            var badShape = CopyTools.Copy(svc, from, null, new[] { "HeadParts", "Factions" }, null, null, "PShape", "PShape");
            Check(Has(badShape, "error:") && Has(badShape, "Factions") && Has(badShape, "RankPlacement"),
                "an unsupported list seed refuses BY NAME, naming the field and what its ENTRIES actually are");
            Check(Has(badShape, "housecarl_apply") && Has(badShape, "Merge") && Has(badShape, "ReplaceAll"),
                "…and names the ROUTE — apply's zip, where replace-vs-merge is the caller's choice");
            Check(Has(badShape, "Nothing was written"), "…and writes nothing");

            // ---- 2. Exactly one destination ------------------------------------------------------------------
            var neither = CopyTools.Copy(svc, from, null, seed, null, null, null, "PNeither");
            Check(Has(neither, "error:") && Has(neither, "EXACTLY ONE destination") && Has(neither, "target=") && Has(neither, "new_editorid="),
                "NEITHER destination refuses, naming both routes");

            var both = CopyTools.Copy(svc, from, null, seed, null, from, "PClone", "PBoth");
            Check(Has(both, "error:") && Has(both, "EXACTLY ONE destination"), "BOTH destinations refuse the same way");

            var wsTarget = CopyTools.Copy(svc, from, null, seed, null, "   ", null, "PWsTarget");
            Check(Has(wsTarget, "error:") && Has(wsTarget, "EXACTLY ONE destination"),
                "a whitespace-only target= is ABSENT, not a destination that then fails to parse");

            // ---- 3. FormID parsing ---------------------------------------------------------------------------
            // No destination is passed either, so this arm also pins the ORDER: the caller is told about `from`
            // first, which is the error they must fix before the destination question even means anything.
            var badFrom = CopyTools.Copy(svc, "notaformid", null, seed, null, null, null, "PBadFrom");
            Check(Has(badFrom, "error:") && Has(badFrom, "bad from") && Has(badFrom, "XXXXXX:Plugin.esp"),
                "a bad from= refuses naming the parameter and the expected shape");
            Check(!badFrom.Contains("EXACTLY ONE destination", StringComparison.OrdinalIgnoreCase),
                "…ahead of the destination gate, so the caller fixes one thing at a time");

            var badTarget = CopyTools.Copy(svc, from, null, seed, null, "nope", null, "PBadTarget");
            Check(Has(badTarget, "error:") && Has(badTarget, "bad target") && Has(badTarget, "XXXXXX:Plugin.esp"),
                "a bad target= refuses naming the parameter and the expected shape");

            // ---- 4. seed_paths is required -------------------------------------------------------------------
            var noSeeds = CopyTools.Copy(svc, from, null, null, null, null, "PNoSeeds", "PNoSeeds");
            Check(Has(noSeeds, "error:") && Has(noSeeds, "seed_paths is required"),
                "seed_paths ABSENT refuses rather than walking from nothing");
            var emptySeeds = CopyTools.Copy(svc, from, null, Array.Empty<string>(), null, null, "PEmpty", "PEmpty");
            Check(Has(emptySeeds, "error:") && Has(emptySeeds, "seed_paths is required"), "…an EMPTY list, the same");
            // A blank ELEMENT is now refused by its index rather than filtered out. The distinction matters because
            // dropping it silently changed the caller's list and shifted the positions later refusals report.
            var blankSeeds = CopyTools.Copy(svc, from, null, new[] { "", "  " }, null, null, "PBlankS", "PBlankS");
            Check(Has(blankSeeds, "error:") && Has(blankSeeds, "seed_paths[0] is blank"),
                "…a BLANK ELEMENT is refused BY INDEX, not silently dropped");
            var blankSeed2 = CopyTools.Copy(svc, from, null, new[] { "HeadParts", " " }, null, null, "PBlankS2", "PBlankS2");
            Check(Has(blankSeed2, "error:") && Has(blankSeed2, "seed_paths[1] is blank"),
                "…and the index names the element the CALLER passed, not a post-filter position");

            var padded = CopyTools.Copy(svc, from, null, new[] { "  HeadParts  " }, null, null, "PPadded", "PPadded");
            Check(Has(padded, "CLONED") && Has(padded, "SrcHair"),
                $"a padded seed path is TRIMMED, not refused as an unknown field ({First(padded)})");

            // ---- 5. from_source — the documented default ------------------------------------------------------
            var defaulted = CopyTools.Copy(svc, from, null, seed, null, null, "PDefSrc", "PDefSrc");
            Check(Has(defaulted, "CLONED"), $"no from_source= still copies ({First(defaulted)})");
            Check(Has(defaulted, "source: winner"),
                "…through the DOCUMENTED default, and the readback names it — 'winner', not silence");
            var emptySrc = CopyTools.Copy(svc, from, Array.Empty<string>(), seed, null, null, "PEmptySrc", "PEmptySrc");
            Check(Has(emptySrc, "source: winner"),
                "an EMPTY from_source= takes the same default rather than reaching the empty-universe refusal");

            // A blank element is DROPPED by the tool, so the chain the service builds is what remains. Pinned
            // because the alternative reading — the service's per-element blank refusal — is reachable only if this
            // filter goes, and which of the two a caller gets should not be an accident of layering.
            var blankSrc = CopyTools.Copy(svc, from, new[] { "  ", "Src.esp" }, seed, null, null, "PBlankSrc", "PBlankSrc");
            Check(Has(blankSrc, "error:") && Has(blankSrc, "from_source[0] is blank"),
                "a blank from_source= element is REFUSED BY INDEX — dropping it shifted every later position");
            var blankSrc2 = CopyTools.Copy(svc, from, new[] { "Src.esp", "" }, seed, null, null, "PBlankSrc2", "PBlankSrc2");
            Check(Has(blankSrc2, "error:") && Has(blankSrc2, "from_source[1] is blank"),
                "…at the caller's own index, which is the whole point of refusing rather than filtering");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  guard threw: " + ex);
            fail++;
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The first line of a tool result — enough to make a failed arm diagnosable without dumping the whole
    /// render into the log.</summary>
    static string First(string result)
    {
        var i = result.IndexOf('\n');
        return (i < 0 ? result : result[..i]).Trim();
    }

    static void WriteModDirect(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }
}

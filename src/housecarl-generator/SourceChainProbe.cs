using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// ORDERED SOURCE UNIVERSE guard (SPEC §3.1 AMENDMENT 2026-08-14). Drives the REAL
/// <c>LoadOrderService.BuildSourceChain</c> over a synthetic MO2 instance — never a re-implementation of the arm
/// logic — and pins what the amendment claims:
///   LENGTH-1     — ["winner"] is the active order's winning version; ["Donor.esp"] is that file's own version.
///                  The two spellings of "today's grammar", proven to still mean today's thing.
///   ORDER        — first hit wins, and the hit NAMES which arm produced it; a key only a later arm has falls
///                  through to it; a key NO arm has misses with EVERY arm consulted, in order.
///   FAULT≠MISS   — an arm that HAS a key but cannot read it STOPS the chain instead of silently substituting a
///                  later arm's version of that record (Q3 — the silent-wrong-answer class the fault type exists for).
///   TRIPWIRE     — an off-order FILE element resolves to the SAME body alone, first in a chain, and second in a
///                  chain. If this arm ever fails, the amendment text is wrong, not this call site (§14).
///   POLES        — previous_provider refuses BY NAME (subject-relative, §4.3, no subject on a walk) and points at
///                  the gap-report path; an unknown name refuses naming where it was searched.
///   NO-SIGIL     — the §5.5 exemption this grammar leans on, pinned rather than asserted (sibling of
///                  read-plugin-file-guard arm 7c): a plugin ACTUALLY NAMED 'winner.esp' is reachable as itself and
///                  does NOT collide with the bare pole, and an EXTENSIONLESS spelling refuses by name and never
///                  fuzzy-matches — with a with-extension control beside it, so the arm cannot pass because
///                  everything errored.
///
/// GUARD-NOTE (branch rule): an arm downstream of a refusal reads it null-SAFELY (`?.`), never with the
/// null-forgiving `!`. An arm that THROWS under sabotage hides every arm behind it, so the sabotage's real
/// blast radius becomes unmeasurable — a RED check on this branch reported 1 failure where the true number
/// was 6. An arm that can throw under sabotage is an arm that lies about severity. The grep is part of the rule,
/// not a one-time cleanup: the sweep that introduced this note also introduced a FRESH `!` one line below a
/// deref it was fixing, caught only by re-running the grep before commit.
/// Run: dotnet run --project src/housecarl-generator -- source-chain-guard
/// </summary>
public static class SourceChainProbe
{
    [CiProbe("source-chain-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  ORDERED SOURCE UNIVERSE guard (SPEC §3.1 amendment)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-source-chain-guard-" + Guid.NewGuid().ToString("N"));
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

            // ---------------------------------------------------------------------------------------------
            // Fixture. Base.esm defines three NPCs. Over.esp (ACTIVE) overrides ONLY 'Shared'. Donor.esp is
            // DISABLED and overrides 'Shared' too, and is the ONLY source of 'DonorOnly'.
            //
            //   key        | Base.esm | Over.esp (active) | Donor.esp (disabled)
            //   Shared     |   Name=B |   Name=O          |   Name=D
            //   BaseOnly   |   Name=B |    —              |    —
            //   DonorOnly  |    —     |    —              |   Name=D      (a Donor-defined record)
            //
            // Every ordering claim below is decided by which NAME comes back, so a wrong arm is visible as a
            // wrong VALUE rather than as a count.
            // ---------------------------------------------------------------------------------------------
            var baseKey = new ModKey("Base", ModType.Master);
            var sharedFk = new FormKey(baseKey, 0x800);
            var baseOnlyFk = new FormKey(baseKey, 0x801);
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            baseMod.Npcs.Add(new Npc(sharedFk, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "B" });
            baseMod.Npcs.Add(new Npc(baseOnlyFk, SkyrimRelease.SkyrimSE) { EditorID = "BaseOnly", Name = "B" });
            WriteModDirect(mods, "BaseMod", baseMod);

            // A plugin ACTUALLY NAMED 'winner.esp' — the hostile fixture for the no-sigil exemption. Friendly
            // fixture names cannot test a name contract (PR #339's lesson), so the collision is made REAL.
            var winnerKey = new ModKey("winner", ModType.Plugin);
            var winnerOnlyFk = new FormKey(winnerKey, 0x800);
            var winnerMod = new SkyrimMod(winnerKey, SkyrimRelease.SkyrimSE);
            winnerMod.Npcs.Add(new Npc(winnerOnlyFk, SkyrimRelease.SkyrimSE) { EditorID = "WinnerOnly", Name = "W" });
            WriteModDirect(mods, "WinnerNamedMod", winnerMod);

            // Over.esp overrides 'Shared' AND winner.esp's own record, and loads ABOVE winner.esp. That second
            // override is what makes the no-sigil arms discriminating: without it winner.esp is the sole provider
            // of its record, so the winner POLE and named('winner.esp') return the same body and a value check
            // cannot tell them apart — a RED check found exactly that arm passing under a fuzzy-pole sabotage.
            var overKey = new ModKey("Over", ModType.Plugin);
            var overMod = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
            overMod.Npcs.Add(new Npc(sharedFk, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "O" });
            overMod.Npcs.Add(new Npc(winnerOnlyFk, SkyrimRelease.SkyrimSE) { EditorID = "WinnerOnly", Name = "OW" });
            WriteModDirect(mods, "OverMod", overMod, baseMod, winnerMod);

            var donorKey = new ModKey("Donor", ModType.Plugin);
            var donorOnlyFk = new FormKey(donorKey, 0xD01);
            var donorMod = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            donorMod.Npcs.Add(new Npc(sharedFk, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "D" });
            donorMod.Npcs.Add(new Npc(donorOnlyFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorOnly", Name = "D" });
            WriteModDirect(mods, "DonorMod", donorMod, baseMod);

            // Donor's mod folder is switched OFF; winner.esp and the rest are on. Donor.esp is therefore reachable
            // ONLY through the off-order file arm — the arm the tripwire is about. Over.esp loads LAST so it wins
            // both the records it overrides.
            File.WriteAllText(Path.Combine(prof, "modlist.txt"),
                "+OverMod\r\n+WinnerNamedMod\r\n+BaseMod\r\n-DonorMod\r\n");
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "Base.esm\r\nwinner.esp\r\nOver.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Base.esm\r\n*winner.esp\r\n*Over.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

            using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));

            static string? NameOf(SourceFetch f) => (f.Hit?.Body as INpcGetter)?.Name?.String;

            // ---- 1. LENGTH-1 — the two spellings of today's grammar --------------------------------------
            svc.WithSourceChainForGuard(new[] { SourcePoles.Winner }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null, $"length-1 ['winner'] builds ({err})");
                Check(chain is { IsSinglePole: true }, "length-1 ['winner'] is the degenerate single-pole chain");
                Check(chain is not null && NameOf(chain.Fetch(sharedFk)) == "O",
                    "['winner'] resolves the ACTIVE ORDER's winning version (Over.esp's 'O', not Base's 'B')");
                Check(chain is not null && NameOf(chain.Fetch(baseOnlyFk)) == "B",
                    "['winner'] resolves a record only the base master has");
                Check(chain is not null && chain.Fetch(donorOnlyFk).IsMiss,
                    "['winner'] MISSES a record no ACTIVE plugin carries (the disabled donor's own record)");
                return 0;
            });

            svc.WithSourceChainForGuard(new[] { "Donor.esp" }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null, $"length-1 ['Donor.esp'] builds — a DISABLED plugin is a legal source ({err})");
                Check(chain is not null && chain.Arms[0].Kind == SourceArmKind.File,
                    "['Donor.esp'] resolved through the off-order FILE arm");
                Check(chain is not null && NameOf(chain.Fetch(sharedFk)) == "D",
                    "['Donor.esp'] resolves THAT FILE's own version ('D'), not the active winner's");
                Check(chain is not null && NameOf(chain.Fetch(donorOnlyFk)) == "D",
                    "['Donor.esp'] resolves a record only that file defines");
                return 0;
            });

            // ---- 2. ORDER — first hit wins, provenance, fallthrough, miss-names-all -----------------------
            svc.WithSourceChainForGuard(new[] { "Over.esp", "Donor.esp" }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null, $"['Over.esp','Donor.esp'] builds ({err})");
                Check(chain is { IsSinglePole: false }, "a 2-element chain is not the single-pole shape");
                var hit = chain?.Fetch(sharedFk);
                Check(hit is not null && NameOf(hit) == "O", "FIRST HIT WINS: a record BOTH arms carry resolves to arm 0's version ('O')");
                Check(hit?.Hit?.ArmIndex == 0 && hit?.Hit?.Arm.Spelling == "Over.esp",
                    "the hit NAMES which arm produced it (index 0, 'Over.esp') — the readback's provenance");
                var fell = chain?.Fetch(donorOnlyFk);
                Check(fell is not null && NameOf(fell) == "D" && fell.Hit?.ArmIndex == 1,
                    "FALLBACK: a record only arm 1 carries resolves to arm 1, and says so");
                var missed = chain?.Fetch(new FormKey(baseKey, 0x8FF));
                Check(missed?.IsMiss == true, "a record NO arm carries is a miss, not a fault");
                var miss = chain?.Miss(new FormKey(baseKey, 0x8FF), "Npc.HeadParts");
                Check(miss?.Consulted.Count == 2
                      && miss.Consulted[0].Spelling == "Over.esp" && miss.Consulted[1].Spelling == "Donor.esp",
                    "the MISS names EVERY arm consulted, in order — not just the last one tried");
                return 0;
            });

            // Order REVERSED — the same two arms, the other way round, must give the other answer. Without this
            // arm, an implementation that always consulted the off-order file would pass every test above.
            svc.WithSourceChainForGuard(new[] { "Donor.esp", "Over.esp" }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null && NameOf(chain.Fetch(sharedFk)) == "D",
                    "ORDER IS THE SEMANTICS: reversing the arms reverses which version wins ('D')");
                return 0;
            });

            // ---- 3. TRIPWIRE — an off-order element resolves identically alone and at either position -----
            string? alone = null, first = null, second = null;
            svc.WithSourceChainForGuard(new[] { "Donor.esp" }, "from_source", (c, e) => alone = c is null ? null : NameOf(c.Fetch(sharedFk)));
            svc.WithSourceChainForGuard(new[] { "Donor.esp", "Over.esp" }, "from_source", (c, e) => first = c is null ? null : NameOf(c.Fetch(sharedFk)));
            svc.WithSourceChainForGuard(new[] { "Over.esp", "Donor.esp" }, "from_source", (c, e) =>
                second = (c?.Arms[1].Fetch(sharedFk) as INpcGetter)?.Name?.String);
            Check(alone == "D" && first == "D" && second == "D",
                $"TRIPWIRE: the off-order file resolves the SAME body alone / first / second (got {alone}/{first}/{second}) — §14");

            // ---- 4. FAULT ≠ MISS — a throwing arm stops the chain, never substitutes ----------------------
            // Hand-built arms: making a real overlay throw on ONE key would need a corrupt fixture, and the claim
            // under test is the CHAIN's rule, not the overlay's parser.
            var faulting = new SourceChain(new[]
            {
                new SourceArm("Faulty.esp", SourceArmKind.File, "file 'Faulty.esp'",
                    _ => throw new InvalidOperationException("a record Mutagen cannot parse")),
                new SourceArm("Fallback.esp", SourceArmKind.File, "file 'Fallback.esp'",
                    _ => baseMod.Npcs.First()),
            });
            var faulted = faulting.Fetch(sharedFk, "Npc.HeadParts");
            Check(faulted.Fault is not null && faulted.Hit is null,
                "a FAULTING arm produces a fault, and NOT a hit from the arm behind it (no silent substitution)");
            Check(faulted.Fault?.ArmIndex == 0 && faulted.Fault?.Arm.Spelling == "Faulty.esp"
                  && faulted.Fault?.Cause.Contains("cannot parse") == true,
                "the fault names the ARM and the CAUSE, and carries the pull chain");
            Check(!faulted.IsMiss, "a fault is NOT reported as a miss — the two have different remedies");

            // ---- 5. POLES — previous_provider, and an unknown name ---------------------------------------
            svc.WithSourceChainForGuard(new[] { "Over.esp", SourcePoles.PreviousProvider }, "from_source", (chain, err) =>
            {
                Check(chain is null && err is not null, "previous_provider as an element REFUSES");
                Check(err?.Contains(SourcePoles.PreviousProvider) == true && err?.Contains("SUBJECT-relative") == true,
                    "the refusal names the token and WHY it has no meaning on a walk");
                Check(err?.Contains("gap report") == true, "the refusal names the gap-report path that would define it");
                Check(err?.Contains("from_source[1]") == true, "the refusal names WHICH element was bad");
                return 0;
            });

            svc.WithSourceChainForGuard(new[] { "NoSuchPlugin.esp" }, "from_source", (chain, err) =>
            {
                Check(chain is null && err is not null, "a name nothing provides REFUSES");
                Check(err?.Contains("NoSuchPlugin.esp") == true, "the refusal quotes the caller's own spelling");
                return 0;
            });

            // ---- 6. NO-SIGIL — the §5.5 exemption, pinned (sibling of read-plugin-file-guard arm 7c) ------
            // 6a. CONTROL: the with-extension spelling of the hostile plugin resolves as ITSELF. Placed first so a
            //     wholesale fixture failure cannot make 6b pass for the wrong reason.
            svc.WithSourceChainForGuard(new[] { "winner.esp" }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null, $"CONTROL: a plugin actually named 'winner.esp' is a legal source ({err})");
                // Discriminating BY VALUE: Over.esp overrides this record and wins it, so the winner POLE would
                // answer "OW" here. Only a true named(plugin) resolution answers "W".
                Check(chain is not null && NameOf(chain.Fetch(winnerOnlyFk)) == "W",
                    "'winner.esp' resolves that PLUGIN's own version ('W'), not the pole's winner ('OW') — the bare pole does not swallow a real plugin's name");
                Check(chain is not null && chain.Arms[0].Kind == SourceArmKind.ActiveOrder
                      && chain.Arms[0].Spelling == "winner.esp",
                    "'winner.esp' resolved as named(plugin), NOT as the winner pole");
                return 0;
            });
            // 6b. The bare token, with that same plugin installed and ACTIVE, still means the POLE — the two name
            //     spaces are disjoint because a plugin name must carry an extension.
            svc.WithSourceChainForGuard(new[] { SourcePoles.Winner }, "from_source", (chain, err) =>
            {
                Check(chain is not null && chain.Arms[0].Kind == SourceArmKind.ActiveOrder
                      && chain.Arms[0].Spelling == SourcePoles.Winner,
                    "bare 'winner' means the POLE even with a plugin named winner.esp installed and active");
                Check(chain is not null && NameOf(chain.Fetch(sharedFk)) == "O",
                    "…and it resolves the load-order winner, not winner.esp's records");
                Check(chain is not null && NameOf(chain.Fetch(winnerOnlyFk)) == "OW",
                    "…including on winner.esp's OWN record, where the pole answers the winner ('OW'), not that file's ('W')");
                return 0;
            });
            // 6c. An EXTENSIONLESS plugin spelling refuses BY NAME and never fuzzy-matches to the real file.
            svc.WithSourceChainForGuard(new[] { "Donor" }, "from_source", (chain, err) =>
            {
                Check(chain is null && err is not null, "an extensionless plugin spelling ('Donor') REFUSES");
                Check(err?.Contains("Donor") == true, "the refusal names the spelling that failed");
                return 0;
            });
            // …and the control for 6c: WITH the extension, that same plugin resolves. Without this pair, 6c would
            // pass identically if every source in the fixture were broken.
            svc.WithSourceChainForGuard(new[] { "Donor.esp" }, "from_source", (chain, err) =>
            {
                Check(err is null && chain is not null && NameOf(chain.Fetch(donorOnlyFk)) == "D",
                    "CONTROL for the extensionless refusal: 'Donor.esp' resolves — the refusal was about the SPELLING");
                return 0;
            });

            // ---- 7. shape refusals -----------------------------------------------------------------------
            svc.WithSourceChainForGuard(Array.Empty<string>(), "from_source", (chain, err) =>
            {
                Check(chain is null && err is not null, "an EMPTY source list refuses at the caller layer");
                Check(err?.Contains("winner") == true, "the empty-list refusal names both element kinds");
                return 0;
            });
            svc.WithSourceChainForGuard(new[] { "Over.esp", "  " }, "from_source", (chain, err) =>
            {
                Check(chain is null && err?.Contains("from_source[1]") == true,
                    "a BLANK element refuses naming its index, never silently dropped");
                return 0;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  guard threw: " + ex);
            fail++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Write a fixture mod into a synthetic MO2 mod folder — the same helper the copy-npc-appearance guard
    /// uses, master table included (a hand-rolled WriteToBinary cannot map a foreign FormKey to a master index).</summary>
    static void WriteModDirect(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }
}

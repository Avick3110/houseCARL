using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the CK-parity default-populate fix (S1 — the confirmed-CK-crash tier;
/// the #131 SNAM fix generalised to the rest of the DIAL/INFO/DLVW family). Mutagen omits a null optional subrecord
/// on write; the Creation Kit writes it unconditionally — so an INFO created without CNAM (FavorLevel) / ENAM (Flags)
/// crashes the CK when its topic is opened, and a bare DLVW crashes the CK Dialogue Views editor. This guard pins the
/// whole S1 surface so it can't drift:
///   • the create-path AUTO-FILL — create INFO/DLVW through the service and read the written subrecords back off disk,
///   • the create-path NON-OVERRIDE — an explicit value is never clobbered,
///   • the QUALIFYING VALUES — FavorLevel=None, materialized Flags, DNAM=00, ENAM=00000000 (the CK's own defaults),
///   • the BNAM lint — housecarl_validate_dialogue WARNS a Custom topic with no Branch (the CK-views-crash catch),
///     and does NOT warn a Custom topic that HAS a branch.
/// Driven over a synthetic MO2 instance in temp (the dialogue-subtype-marker-guard synth pattern; no game files needed).
/// Run: dotnet run --project src/housecarl-generator -- dialogue-ckparity-guard
///
/// Arms (ALL required):
///   INFO-AUTOFILL     — create a topic + nested INFO with only Prompt → written INFO carries FavorLevel=None + a
///                       materialized Flags (ENAM), and BOTH auto-fills are REPORTED as ops (not silent, Q3).
///   INFO-FAVOR-WINS   — an explicit FavorLevel=Large is NOT overridden to None (Flags still auto-fills).
///   INFO-FLAGS-WINS   — an explicit Flags (ResetHours=3) is NOT reset by the auto-fill (FavorLevel still auto-fills).
///   DLVW-AUTOFILL     — create a bare DialogView → written DNAM=00, ENAM=00000000, both REPORTED.
///   DLVW-DNAM-WINS    — an explicit DNAM=FF is NOT overridden to 00 (ENAM still auto-fills).
///   BNAM-LINT         — a Custom topic with no Branch → validate_dialogue raises a Warning naming Branch (BNAM); a
///                       Custom topic WITH a branch raises no such Warning.
/// </summary>
internal static class DialogueCkParityGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — dialogue CK-parity default-populate (S1)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- CONST-SHAPE: the pinned byte defaults are exactly what a CK-authored DialogView carries. ----
        Check(DialogueCkParity.ViewDnamHex == "00" && DialogueCkParity.ViewEnamHex == "00000000",
            $"CONST-SHAPE DLVW DNAM/ENAM defaults are 00 / 00000000 — dnam={DialogueCkParity.ViewDnamHex} enam={DialogueCkParity.ViewEnamHex}");

        var root = Path.Combine(Path.GetTempPath(), "hc-dial-ckparity-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // --- synthetic MO2 instance with a master mod carrying two Custom topics (one branch-less, one branched)
            //     + a DLBR for the branched one — the fixtures the BNAM-lint arm validates. ---
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcCkpMaster", ModType.Master);
            var modDir = Path.Combine(mods, "MasterMod");
            var masterPath = Path.Combine(modDir, mKey.FileName.String);
            Directory.CreateDirectory(modDir);
            FormKey noBranchFk, branchedFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var branch = m.DialogBranches.AddNew(); branch.EditorID = "HcCkpBranch";

                var noBranch = m.DialogTopics.AddNew(); noBranch.EditorID = "HcCkpNoBranch";
                noBranch.Subtype = DialogTopic.SubtypeEnum.Custom;
                noBranch.SubtypeName = new RecordType("CUST");          // well-formed marker — isolate the BNAM finding
                // Branch LEFT UNSET — the CK-views-crash shape the lint must warn on.
                noBranchFk = noBranch.FormKey;

                var branched = m.DialogTopics.AddNew(); branched.EditorID = "HcCkpBranched";
                branched.Subtype = DialogTopic.SubtypeEnum.Custom;
                branched.SubtypeName = new RecordType("CUST");
                branched.Branch.SetTo(branch.FormKey);                  // wired to its branch — no BNAM warning expected
                branchedFk = branched.FormKey;

                m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            // ---- INFO-AUTOFILL: create a topic + nested INFO with only a Prompt → written INFO has FavorLevel=None +
            //      a materialized Flags (ENAM present), and BOTH fills are reported as ops. ----
            {
                var recs = new[]
                {
                    new CreateOp { RecordType = "DialogTopic", Editorid = "HcCkpAfTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" } } },
                    new CreateOp { RecordType = "DialogResponses", Editorid = "HcCkpAfInfo", Parent = "HcCkpAfTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Prompt", Verb = "Set", Value = "Hello there." } } },
                };
                var o = svc.CreateRecordsBatch(recs, "HcCkpAf", null, fullReadback: false);
                var infoRec = o.Success ? o.Created.FirstOrDefault(c => c.EditorId == "HcCkpAfInfo") : null;
                var (favor, hasFlags, _) = infoRec is not null ? ReadInfo(o.OutputPath, infoRec.FormKey) : (null, false, 0f);
                bool favorReported = infoRec is not null && infoRec.Ops.Any(op => op.Label.Contains("FavorLevel", StringComparison.OrdinalIgnoreCase));
                bool flagsReported = infoRec is not null && infoRec.Ops.Any(op => op.Label.Contains("ENAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && favor == FavorLevel.None && hasFlags && favorReported && flagsReported,
                    $"INFO-AUTOFILL bare INFO → FavorLevel=None + Flags(ENAM) present, both reported — {(o.Success ? $"favor={favor} hasFlags={hasFlags} favorReported={favorReported} flagsReported={flagsReported}" : "err=[" + o.Error + "]")}");
            }

            // ---- INFO-FAVOR-WINS: an explicit FavorLevel=Large is not overridden to None (Flags still auto-fills). ----
            {
                var recs = new[]
                {
                    new CreateOp { RecordType = "DialogTopic", Editorid = "HcCkpFvTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" } } },
                    new CreateOp { RecordType = "DialogResponses", Editorid = "HcCkpFvInfo", Parent = "HcCkpFvTopic",
                        Operations = new[] { new BulkOp { FieldPath = "FavorLevel", Verb = "Set", Value = "Large" } } },
                };
                var o = svc.CreateRecordsBatch(recs, "HcCkpFv", null, fullReadback: false);
                var infoRec = o.Success ? o.Created.FirstOrDefault(c => c.EditorId == "HcCkpFvInfo") : null;
                var (favor, hasFlags, _) = infoRec is not null ? ReadInfo(o.OutputPath, infoRec.FormKey) : (null, false, 0f);
                Check(o.Success && favor == FavorLevel.Large && hasFlags,
                    $"INFO-FAVOR-WINS explicit FavorLevel=Large kept (not overridden to None), Flags still filled — {(o.Success ? $"favor={favor} hasFlags={hasFlags}" : "err=[" + o.Error + "]")}");
            }

            // ---- INFO-FLAGS-WINS: an explicit Flags (ResetHours=3) is not reset by the auto-fill; FavorLevel still fills. ----
            {
                var recs = new[]
                {
                    new CreateOp { RecordType = "DialogTopic", Editorid = "HcCkpFlTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" } } },
                    new CreateOp { RecordType = "DialogResponses", Editorid = "HcCkpFlInfo", Parent = "HcCkpFlTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Flags.ResetHours", Verb = "Set", Value = "3" } } },
                };
                var o = svc.CreateRecordsBatch(recs, "HcCkpFl", null, fullReadback: false);
                var infoRec = o.Success ? o.Created.FirstOrDefault(c => c.EditorId == "HcCkpFlInfo") : null;
                var (favor, hasFlags, reset) = infoRec is not null ? ReadInfo(o.OutputPath, infoRec.FormKey) : (null, false, 0f);
                Check(o.Success && hasFlags && Math.Abs(reset - 3f) < 0.001f && favor == FavorLevel.None,
                    $"INFO-FLAGS-WINS explicit Flags.ResetHours=3 kept (not reset), FavorLevel still filled — {(o.Success ? $"reset={reset} favor={favor} hasFlags={hasFlags}" : "err=[" + o.Error + "]")}");
            }

            // ---- DLVW-AUTOFILL: create a bare DialogView → written DNAM=00, ENAM=00000000, both reported. ----
            {
                var o = svc.CreateRecords("DialogView", "HcCkpView", Array.Empty<BulkOp>(), "HcCkpView", null);
                var (dnam, enam) = o.Success ? ReadView(o.OutputPath, o.Created[0].FormKey) : (null, null);
                bool dnamReported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("DNAM", StringComparison.OrdinalIgnoreCase));
                bool enamReported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("ENAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && dnam == "00" && enam == "00000000" && dnamReported && enamReported,
                    $"DLVW-AUTOFILL bare DialogView → DNAM=00 ENAM=00000000, both reported — {(o.Success ? $"dnam={dnam} enam={enam} dnamReported={dnamReported} enamReported={enamReported}" : "err=[" + o.Error + "]")}");
            }

            // ---- DLVW-DNAM-WINS: an explicit DNAM=FF is not overridden to 00 (ENAM still auto-fills to 00000000). ----
            {
                var ops = new[] { new BulkOp { FieldPath = "DNAM", Verb = "Set", Value = "FF" } };
                var o = svc.CreateRecords("DialogView", "HcCkpViewDnam", ops, "HcCkpViewDnam", null);
                var (dnam, enam) = o.Success ? ReadView(o.OutputPath, o.Created[0].FormKey) : (null, null);
                Check(o.Success && dnam == "FF" && enam == "00000000",
                    $"DLVW-DNAM-WINS explicit DNAM=FF kept (not overridden to 00), ENAM still filled — {(o.Success ? $"dnam={dnam} enam={enam}" : "err=[" + o.Error + "]")}");
            }

            // ---- BNAM-LINT: a Custom topic with no Branch → a Warning naming Branch (BNAM); a Custom topic WITH a
            //      branch → no such Warning. ----
            {
                var rNo = svc.ValidateDialogue(noBranchFk);
                bool warned = rNo.Topics.Count == 1 && rNo.Topics[0].Issues.Any(i =>
                    i.Severity == DialogueIssueSeverity.Warning &&
                    i.Message.Contains("Branch (BNAM)", StringComparison.OrdinalIgnoreCase) &&
                    i.Message.Contains("Dialogue Views", StringComparison.OrdinalIgnoreCase));
                var rYes = svc.ValidateDialogue(branchedFk);
                bool clean = rYes.Topics.Count == 1 && !rYes.Topics[0].Issues.Any(i =>
                    i.Message.Contains("Branch (BNAM)", StringComparison.OrdinalIgnoreCase));
                Check(warned && clean,
                    $"BNAM-LINT branch-less Custom topic → Warning; branched Custom topic → clean — warned={warned} clean={clean}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  guard infrastructure: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            fail++;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== dialogue-ckparity-guard: {(fail == 0 ? "PASS" : "FAIL")} ===");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Read a created INFO's CK-parity fields back off the written patch on disk: (FavorLevel?, whether the
    /// Flags/ENAM struct is present, its ResetHours). The INFO is nested in its topic's Responses, so it's found via
    /// EnumerateMajorRecords, not a top-level group.</summary>
    static (FavorLevel? favor, bool hasFlags, float reset) ReadInfo(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var info = ov.EnumerateMajorRecords<IDialogResponsesGetter>().FirstOrDefault(x => x.FormKey == infoFk);
            if (info is null) return (null, false, 0f);
            return (info.FavorLevel, info.Flags is not null, info.Flags?.ResetHours ?? 0f);
        }
        catch { return (null, false, 0f); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Read a created DialogView's DNAM/ENAM bytes back off the written patch as hex (null when the subrecord
    /// is absent) — the bytes MO2 would load.</summary>
    static (string? dnam, string? enam) ReadView(string patchPath, FormKey viewFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var view = ov.DialogViews.FirstOrDefault(x => x.FormKey == viewFk);
            if (view is null) return (null, null);
            string? Hex(Noggog.ReadOnlyMemorySlice<byte>? b) => b is { } m ? Convert.ToHexString(m.ToArray()) : null;
            return (Hex(view.DNAM), Hex(view.ENAM));
        }
        catch { return (null, null); }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}

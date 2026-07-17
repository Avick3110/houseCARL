using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the CK-parity default-populate fix across the DIAL/INFO/DLVW/DLBR/QUST
/// family (S1 — the confirmed-CK-crash tier; S2 — the byte-only tier; the #131 SNAM fix generalised). Mutagen omits a
/// null optional subrecord on write; the Creation Kit writes it unconditionally — so an INFO created without CNAM
/// (FavorLevel) / ENAM (Flags) crashes the CK when its topic is opened, and a bare DLVW crashes the CK Dialogue Views
/// editor (S1); the S2 fields (DLBR Category, DIAL Priority, QUST NextAliasID + objective Flags) are byte-parity only
/// (no crash) but complete the write the same way; the S3 field (DLBR Flags/DNAM, #212) is neither — an absent DNAM is
/// read by the ENGINE as TopLevel, so a byte-valid branch that passes every check misbehaves in the running game.
/// This guard pins the whole surface so it can't drift:
///   • the create-path AUTO-FILL — create the record through the service and read the written subrecords back off disk,
///   • the create-path NON-OVERRIDE — an explicit value is never clobbered,
///   • the QUALIFYING VALUES — FavorLevel=None, materialized Flags, DNAM=00, ENAM=00000000 (S1); Category=Player,
///     Priority=50, NextAliasID=max+1/0, objective Flags=0 (S2) — the CK's own defaults, byte-verified vs vanilla,
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
///   INFO-GAP-PROBE    — the anti-drift tie: MissingInfoDefaults (the validator's check side) and ApplyInfoDefaults
///                       (the fill side) read ONE shared presence predicate — a bare INFO reports exactly the CNAM+ENAM
///                       gaps, and after the fill reports none (a field in one path but not the other breaks this).
///   DLVW-GAP-PROBE    — same anti-drift tie for the DLVW: a bare DialogView reports exactly the DNAM+ENAM gaps
///                       via MissingViewDefaults, none after ApplyViewDefaults (shared presence predicates).
///   DLBR-GAP-PROBE    — same tie for the DLBR: a bare DialogBranch reports exactly the TNAM + DNAM gaps via
///                       MissingBranchDefaults, none after ApplyBranchDefaults.
///   QUST-GAP-PROBE    — same tie for the QUST: a bare quest with one Flags-less objective AND one bare alias reports
///                       exactly the ANAM + objective-FNAM + alias-FNAM + alias-VTCK gaps via MissingQuestDefaults,
///                       none after ApplyQuestDefaults.
///   DLVW-AUTOFILL     — create a bare DialogView → written DNAM=00, ENAM=00000000, both REPORTED.
///   DLVW-DNAM-WINS    — an explicit DNAM=FF is NOT overridden to 00 (ENAM still auto-fills).
///   BNAM-LINT         — a Custom topic with no Branch → validate_dialogue raises a Warning naming Branch (BNAM); a
///                       Custom topic WITH a branch raises no such Warning.
///   DLBR-TNAM-AUTOFILL — create a bare DialogBranch → written Category=Player (TNAM), REPORTED.
///   DLBR-TNAM-WINS     — an explicit Category=Command is NOT overridden to Player.
///   DLBR-DNAM-AUTOFILL — (S3, #212) create a bare DialogBranch → Flags=0 (DNAM) PRESENT on disk, REPORTED. Presence
///                       is the assertion, not value: an absent DNAM reads as TopLevel in the ENGINE and publishes the
///                       branch to the player's dialogue menu, while the fill's value is the zero-value — so only a
///                       null-vs-zero read distinguishes fixed from broken (the QUST-ALIAS VTCK round-trip lesson).
///   DLBR-DNAM-WINS     — an explicit Flags=TopLevel is NOT overridden to 0 (a real top-level branch stays visible).
///   DIAL-PNAM-AUTOFILL — create a Custom topic with no Priority → written Priority=50 (PNAM), REPORTED.
///   DIAL-PNAM-WINS     — an explicit Priority=10 is NOT overridden to 50, AND an explicit Priority=0 STAYS 0 (the
///                       non-nullable-float edge: author-set-0 is distinguished from unset, no fill, no op).
///   QUST-ANAM-EMPTY    — create an alias-less Quest → written NextAliasID=0 (ANAM), REPORTED.
///   QUST-ANAM-DERIVE   — (direct) ApplyQuestDefaults on a quest with alias IDs {0,3} → NextAliasID=4 (max+1).
///   QUST-ANAM-WINS     — (direct) an explicit NextAliasID=9 is NOT overridden by the derive.
///   QUST-FNAM-AUTOFILL — (direct) an objective with no Flags → Flags materialised to 0, REPORTED; an explicit
///                       objective Flags=OrWithPrevious is NOT reset.
///   QUST-ALIAS-AUTOFILL— a quest with a bare alias → the alias FNAM (Flags=0) + VTCK (VoiceTypes=null link) are
///                       materialised, REPORTED, and — the empirical crux — BOTH survive a disk round-trip (a
///                       present-but-null VTCK is serialised, not skipped). The HCBR-2026-07-10 fix.
///   QUST-ALIAS-WINS    — (direct) an alias with explicit Flags + explicit VoiceTypes is NOT overridden (no fill).
///   QUST-ALIAS-LOCATION— (direct) a Location-type alias gets FNAM (Flags=0) but NOT VTCK (VTCK is scoped to reference
///                       aliases); MissingQuestDefaults raises no VTCK gap for it (no false-warn on CK location aliases).
/// </summary>
internal static class DialogueCkParityGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — dialogue CK-parity default-populate (S1+S2)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- CONST-SHAPE: the pinned byte defaults are exactly what a CK-authored DialogView carries. ----
        Check(DialogueCkParity.ViewDnamHex == "00" && DialogueCkParity.ViewEnamHex == "00000000",
            $"CONST-SHAPE DLVW DNAM/ENAM defaults are 00 / 00000000 — dnam={DialogueCkParity.ViewDnamHex} enam={DialogueCkParity.ViewEnamHex}");

        // ---- CONST-SHAPE (S2): the pinned seed values are the CK-authored/vanilla defaults (byte-verified 2026-07-04). ----
        Check(DialogueCkParity.TopicPrioritySeed == 50f && DialogueCkParity.BranchCategoryDefault == DialogBranch.CategoryType.Player,
            $"CONST-SHAPE S2 seeds — DIAL Priority=50, DLBR Category=Player — priority={DialogueCkParity.TopicPrioritySeed} category={DialogueCkParity.BranchCategoryDefault}");

        // ---- CONST-SHAPE (S3): the DLBR Flags seed is 0 — NO flag set. The value matters in a way the others don't:
        //      any non-zero seed here would set TopLevel/Blocking/Exclusive on every authored branch, which is the
        //      #212 defect with a different sign. Pinned explicitly so a "helpful" default can't drift in. ----
        Check(DialogueCkParity.BranchFlagsDefault == default(DialogBranch.Flag)
            && !DialogueCkParity.BranchFlagsDefault.HasFlag(DialogBranch.Flag.TopLevel),
            $"CONST-SHAPE S3 seed — DLBR Flags=0 (no TopLevel/Blocking/Exclusive) — flags={DialogueCkParity.BranchFlagsDefault} raw={(int)DialogueCkParity.BranchFlagsDefault}");

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

            // ---- INFO-GAP-PROBE: the anti-drift tie. MissingInfoDefaults (the validator's check side, added with the
            //      validate_dialogue INFO CK-parity lint) and ApplyInfoDefaults (the create-path fill side) read ONE
            //      shared presence predicate, so they can't drift: a bare INFO reports EXACTLY the CNAM+ENAM gaps, and
            //      after the fill reports NONE. A field added to the fill path but not the check (or vice-versa) breaks
            //      this. Pure logic — no service/disk. ----
            {
                var info = new DialogResponses(FormKey.Factory("000800:HcCkpGapProbe.esm"), SkyrimRelease.SkyrimSE);
                var before = DialogueCkParity.MissingInfoDefaults(info);
                bool flaggedBoth = before.Count == 2
                    && before.Any(g => g.Subrecord.Contains("CNAM", StringComparison.OrdinalIgnoreCase))
                    && before.Any(g => g.Subrecord.Contains("ENAM", StringComparison.OrdinalIgnoreCase));
                DialogueCkParity.ApplyInfoDefaults(info);
                int after = DialogueCkParity.MissingInfoDefaults(info).Count;
                Check(flaggedBoth && after == 0,
                    $"INFO-GAP-PROBE bare INFO → CNAM+ENAM gaps (before={before.Count}), none after fill (after={after}) — check/fill share one predicate");
            }

            // ---- DLVW-GAP-PROBE: the same anti-drift tie for the DialogView — MissingViewDefaults (the validator's
            //      check side) and ApplyViewDefaults (the fill side) read the SAME presence predicates: a bare DLVW
            //      reports exactly the DNAM+ENAM gaps, none after the fill. Pure logic — no service/disk. ----
            {
                var view = new DialogView(FormKey.Factory("000801:HcCkpGapProbe.esm"), SkyrimRelease.SkyrimSE);
                var before = DialogueCkParity.MissingViewDefaults(view);
                bool flaggedBoth = before.Count == 2
                    && before.Any(g => g.Subrecord.Contains("DNAM", StringComparison.OrdinalIgnoreCase))
                    && before.Any(g => g.Subrecord.Contains("ENAM", StringComparison.OrdinalIgnoreCase));
                DialogueCkParity.ApplyViewDefaults(view);
                int after = DialogueCkParity.MissingViewDefaults(view).Count;
                Check(flaggedBoth && after == 0,
                    $"DLVW-GAP-PROBE bare DialogView → DNAM+ENAM gaps (before={before.Count}), none after fill (after={after}) — check/fill share one predicate");
            }

            // ---- DLBR-GAP-PROBE: the same tie for the DialogBranch — a bare DLBR reports exactly the TNAM gap,
            //      none after ApplyBranchDefaults. ----
            {
                var br = new DialogBranch(FormKey.Factory("000802:HcCkpGapProbe.esm"), SkyrimRelease.SkyrimSE);
                var before = DialogueCkParity.MissingBranchDefaults(br);
                bool flagged = before.Count == 2
                    && before.Any(g => g.Subrecord.Contains("TNAM", StringComparison.OrdinalIgnoreCase))
                    && before.Any(g => g.Subrecord.Contains("DNAM", StringComparison.OrdinalIgnoreCase));
                DialogueCkParity.ApplyBranchDefaults(br);
                int after = DialogueCkParity.MissingBranchDefaults(br).Count;
                Check(flagged && after == 0,
                    $"DLBR-GAP-PROBE bare DialogBranch → TNAM+DNAM gaps (before={before.Count}), none after fill (after={after}) — check/fill share one predicate");
            }

            // ---- QUST-GAP-PROBE: the same tie for the Quest — a bare quest with one Flags-less objective AND one bare
            //      alias reports exactly the ANAM + objective-FNAM + alias-FNAM + alias-VTCK gaps, none after
            //      ApplyQuestDefaults. ----
            {
                var q = new Quest(FormKey.Factory("000803:HcCkpGapProbe.esm"), SkyrimRelease.SkyrimSE);
                q.Objectives.Add(new QuestObjective { Index = 10 });   // Flags null
                q.Aliases.Add(new QuestAlias { ID = 0 });              // Flags + VoiceTypes both null
                var before = DialogueCkParity.MissingQuestDefaults(q);
                bool flaggedAll = before.Count == 4
                    && before.Any(g => g.Subrecord.Contains("ANAM", StringComparison.OrdinalIgnoreCase))
                    && before.Any(g => g.Subrecord.Contains("FNAM", StringComparison.OrdinalIgnoreCase) && g.Subrecord.Contains("Objectives[0]", StringComparison.Ordinal))
                    && before.Any(g => g.Subrecord.Contains("FNAM", StringComparison.OrdinalIgnoreCase) && g.Subrecord.Contains("Aliases[0]", StringComparison.Ordinal))
                    && before.Any(g => g.Subrecord.Contains("VTCK", StringComparison.OrdinalIgnoreCase) && g.Subrecord.Contains("Aliases[0]", StringComparison.Ordinal));
                DialogueCkParity.ApplyQuestDefaults(q);
                int after = DialogueCkParity.MissingQuestDefaults(q).Count;
                Check(flaggedAll && after == 0,
                    $"QUST-GAP-PROBE bare Quest+objective+alias → ANAM+objFNAM+aliasFNAM+aliasVTCK gaps (before={before.Count}), none after fill (after={after}) — check/fill share one predicate");
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

            // ================================  S2 — the byte-only tier  ================================

            // ---- DLBR-TNAM-AUTOFILL: create a bare DialogBranch → written Category=Player (TNAM), REPORTED. ----
            {
                var o = svc.CreateRecords("DialogBranch", "HcCkpBr", Array.Empty<BulkOp>(), "HcCkpBr", null);
                var (cat, _) = o.Success ? ReadBranch(o.OutputPath, o.Created[0].FormKey) : (null, null);
                bool reported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("Category (TNAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && cat == DialogBranch.CategoryType.Player && reported,
                    $"DLBR-TNAM-AUTOFILL bare DialogBranch → Category=Player, reported — {(o.Success ? $"category={cat} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- DLBR-TNAM-WINS: an explicit Category=Command is NOT overridden to Player. ----
            {
                var ops = new[] { new BulkOp { FieldPath = "Category", Verb = "Set", Value = "Command" } };
                var o = svc.CreateRecords("DialogBranch", "HcCkpBrCmd", ops, "HcCkpBrCmd", null);
                var (cat, _) = o.Success ? ReadBranch(o.OutputPath, o.Created[0].FormKey) : (null, null);
                Check(o.Success && cat == DialogBranch.CategoryType.Command,
                    $"DLBR-TNAM-WINS explicit Category=Command kept (not overridden to Player) — {(o.Success ? $"category={cat}" : "err=[" + o.Error + "]")}");
            }

            // ================================  S3 — the in-game-behavior tier (#212)  ================================

            // ---- DLBR-DNAM-AUTOFILL: create a bare DialogBranch → written Flags=0 (DNAM), REPORTED. The empirical
            //      crux mirrors the QUST alias VTCK arm: the fill's value IS the zero-value, so "filled correctly" and
            //      "omitted entirely" are indistinguishable by value alone — only PRESENCE on disk separates the fix
            //      from the bug (absent → engine reads TopLevel → the branch is published to the player's dialogue
            //      menu, which is the whole defect). Assert the round-tripped Flags is NON-NULL *and* zero. ----
            {
                var o = svc.CreateRecords("DialogBranch", "HcCkpBrFl", Array.Empty<BulkOp>(), "HcCkpBrFl", null);
                var (_, flags) = o.Success ? ReadBranch(o.OutputPath, o.Created[0].FormKey) : (null, null);
                bool reported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("Flags (DNAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && flags is not null && flags == default(DialogBranch.Flag) && reported,
                    $"DLBR-DNAM-AUTOFILL bare DialogBranch → Flags=0 PRESENT on disk (not omitted), reported — {(o.Success ? $"flags={(flags is null ? "(absent)" : flags.ToString())} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- DLBR-DNAM-WINS: an explicit Flags=TopLevel is NOT overridden to 0. The non-override arm that matters
            //      most here — a player-facing branch is authored by TICKING TopLevel, and a fill that clobbered it
            //      would hide dialogue that is supposed to show (the #212 defect inverted). Category still auto-fills. ----
            {
                var ops = new[] { new BulkOp { FieldPath = "Flags", Verb = "Set", Value = "TopLevel" } };
                var o = svc.CreateRecords("DialogBranch", "HcCkpBrTop", ops, "HcCkpBrTop", null);
                var (cat, flags) = o.Success ? ReadBranch(o.OutputPath, o.Created[0].FormKey) : (null, null);
                Check(o.Success && flags == DialogBranch.Flag.TopLevel && cat == DialogBranch.CategoryType.Player,
                    $"DLBR-DNAM-WINS explicit Flags=TopLevel kept (not overridden to 0), Category still filled — {(o.Success ? $"flags={flags} category={cat}" : "err=[" + o.Error + "]")}");
            }

            // ---- DIAL-PNAM-AUTOFILL: create a Custom topic with no Priority → written Priority=50 (PNAM), REPORTED. ----
            {
                var ops = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" } };
                var o = svc.CreateRecords("DialogTopic", "HcCkpPnam", ops, "HcCkpPnam", null);
                var prio = o.Success ? ReadTopicPriority(o.OutputPath, o.Created[0].FormKey) : (float?)null;
                bool reported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("Priority (PNAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && prio == 50f && reported,
                    $"DIAL-PNAM-AUTOFILL bare Custom topic → Priority=50, reported — {(o.Success ? $"priority={prio} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- DIAL-PNAM-WINS: explicit Priority=10 kept (not overridden to 50), AND explicit Priority=0 STAYS 0
            //      (the non-nullable-float edge — author-set-0 is distinguished from unset: no fill, no op). ----
            {
                var ops10 = new[]
                {
                    new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" },
                    new BulkOp { FieldPath = "Priority", Verb = "Set", Value = "10" },
                };
                var o10 = svc.CreateRecords("DialogTopic", "HcCkpPnam10", ops10, "HcCkpPnam10", null);
                var prio10 = o10.Success ? ReadTopicPriority(o10.OutputPath, o10.Created[0].FormKey) : (float?)null;

                var ops0 = new[]
                {
                    new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Custom" },
                    new BulkOp { FieldPath = "Priority", Verb = "Set", Value = "0" },
                };
                var o0 = svc.CreateRecords("DialogTopic", "HcCkpPnam0", ops0, "HcCkpPnam0", null);
                var prio0 = o0.Success ? ReadTopicPriority(o0.OutputPath, o0.Created[0].FormKey) : (float?)null;
                bool zeroNotReported = o0.Success && !o0.Created[0].Ops.Any(op => op.Label.Contains("Priority (PNAM", StringComparison.OrdinalIgnoreCase));
                Check(o10.Success && prio10 == 10f && o0.Success && prio0 == 0f && zeroNotReported,
                    $"DIAL-PNAM-WINS explicit Priority=10 kept + explicit Priority=0 stays 0 (no fill/op) — {(o10.Success && o0.Success ? $"p10={prio10} p0={prio0} zeroNotReported={zeroNotReported}" : "err10=[" + o10.Error + "] err0=[" + o0.Error + "]")}");
            }

            // ---- QUST-ANAM-EMPTY: create an alias-less Quest → written NextAliasID=0 (ANAM), REPORTED. ----
            {
                var o = svc.CreateRecords("Quest", "HcCkpQ", Array.Empty<BulkOp>(), "HcCkpQ", null);
                var (nextId, _) = o.Success ? ReadQuest(o.OutputPath, o.Created[0].FormKey) : (null, 0);
                bool reported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("NextAliasID (ANAM", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && nextId == 0u && reported,
                    $"QUST-ANAM-EMPTY alias-less Quest → NextAliasID=0, reported — {(o.Success ? $"nextId={nextId} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- QUST-ANAM-DERIVE + QUST-ANAM-WINS (direct): ApplyQuestDefaults on hand-built quests. The max+1 derive
            //      needs aliases with chosen IDs, cleanest asserted against the authority directly (the create-lane
            //      WIRING is already proven by QUST-ANAM-EMPTY above). ----
            {
                var m2 = new SkyrimMod(new ModKey("HcCkpDirect", ModType.Master), SkyrimRelease.SkyrimSE);

                var qDerive = m2.Quests.AddNew();
                qDerive.Aliases.Add(new QuestAlias { ID = 0 });
                qDerive.Aliases.Add(new QuestAlias { ID = 3 });   // sparse: max is 3, not count-1
                var dfills = DialogueCkParity.ApplyQuestDefaults(qDerive);
                bool deriveReported = dfills.Any(f => f.Label.Contains("NextAliasID (ANAM", StringComparison.OrdinalIgnoreCase));
                Check(qDerive.NextAliasID == 4u && deriveReported,
                    $"QUST-ANAM-DERIVE alias IDs {{0,3}} → NextAliasID=4 (max+1), reported — nextId={qDerive.NextAliasID} reported={deriveReported}");

                var qWins = m2.Quests.AddNew();
                qWins.NextAliasID = 9;
                qWins.Aliases.Add(new QuestAlias { ID = 0 });
                var wfills = DialogueCkParity.ApplyQuestDefaults(qWins);
                bool anamWinNotReported = !wfills.Any(f => f.Label.Contains("NextAliasID (ANAM", StringComparison.OrdinalIgnoreCase));
                Check(qWins.NextAliasID == 9u && anamWinNotReported,
                    $"QUST-ANAM-WINS explicit NextAliasID=9 kept (not derived to 1) — nextId={qWins.NextAliasID} notReported={anamWinNotReported}");

                // ---- QUST-FNAM-AUTOFILL: an objective with no Flags → materialised to 0, REPORTED; an explicit
                //      Flags=OrWithPrevious is NOT reset. ----
                var qObj = m2.Quests.AddNew();
                qObj.NextAliasID = 0;                                  // isolate the FNAM finding (no ANAM fill)
                qObj.Objectives.Add(new QuestObjective { Index = 10 });                                   // Flags null
                qObj.Objectives.Add(new QuestObjective { Index = 20, Flags = QuestObjective.Flag.OrWithPrevious }); // explicit
                var ofills = DialogueCkParity.ApplyQuestDefaults(qObj);
                bool obj0Filled = qObj.Objectives[0].Flags == default(QuestObjective.Flag);
                bool obj1Kept = qObj.Objectives[1].Flags == QuestObjective.Flag.OrWithPrevious;
                bool fnamReported = ofills.Count(f => f.Label.Contains("Flags (FNAM", StringComparison.OrdinalIgnoreCase)) == 1;
                Check(obj0Filled && obj1Kept && fnamReported,
                    $"QUST-FNAM-AUTOFILL null objective Flags→0 (reported), explicit OrWithPrevious kept — obj0={qObj.Objectives[0].Flags} obj1={qObj.Objectives[1].Flags} reportedOnce={fnamReported}");
            }

            // ---- QUST-ALIAS-AUTOFILL: a quest with a bare alias (no Flags/VoiceTypes) → the alias FNAM (Flags=0) and
            //      VTCK (VoiceTypes=null link) are materialised, REPORTED, and — the empirical crux of the HCBR-2026-07-10
            //      fix — BOTH survive a disk round-trip. VTCK is the risky one: a present-but-null link (FormKey.Null)
            //      must be SERIALISED, not skipped as "no value". Write it out, re-read off the binary overlay, confirm
            //      both subrecords are present. ----
            {
                var mA = new SkyrimMod(new ModKey("HcCkpAlias", ModType.Master), SkyrimRelease.SkyrimSE);
                var qA = mA.Quests.AddNew(); qA.EditorID = "HcCkpAliasQ";
                qA.NextAliasID = 0;                                                 // isolate the alias findings (no ANAM fill)
                qA.Aliases.Add(new QuestAlias { ID = 0, Name = "PlayerAlias" });    // Flags + VoiceTypes both null
                var afills = DialogueCkParity.ApplyQuestDefaults(qA);
                bool flagsFilled = qA.Aliases[0].Flags == default(QuestAlias.Flag);
                bool voiceFilled = qA.Aliases[0].VoiceTypes.FormKeyNullable is not null;
                bool fnamReported = afills.Any(f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal) && f.Label.Contains("FNAM", StringComparison.OrdinalIgnoreCase));
                bool vtckReported = afills.Any(f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal) && f.Label.Contains("VTCK", StringComparison.OrdinalIgnoreCase));

                var aliasPath = Path.Combine(root, "HcCkpAlias.esm");
                mA.BeginWrite.ToPath(aliasPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                bool diskFnam = false, diskVtck = false;
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(aliasPath, SkyrimRelease.SkyrimSE);
                    var ar = ov.Quests.FirstOrDefault(x => x.FormKey == qA.FormKey)?.Aliases.FirstOrDefault(x => x.ID == 0);
                    diskFnam = ar?.Flags is not null;
                    diskVtck = ar?.VoiceTypes.FormKeyNullable is not null;
                }
                catch { }
                finally { (ov as IDisposable)?.Dispose(); }

                Check(flagsFilled && voiceFilled && fnamReported && vtckReported && diskFnam && diskVtck,
                    $"QUST-ALIAS-AUTOFILL bare alias → FNAM=0 + VTCK=null-link materialised + reported + survive disk round-trip — "
                    + $"flags={flagsFilled} voice={voiceFilled} fnamRep={fnamReported} vtckRep={vtckReported} diskFNAM={diskFnam} diskVTCK={diskVtck}");
            }

            // ---- QUST-ALIAS-WINS: an alias with explicit Flags + explicit VoiceTypes is NOT overridden (no fill). ----
            {
                var mW = new SkyrimMod(new ModKey("HcCkpAliasWins", ModType.Master), SkyrimRelease.SkyrimSE);
                var qW = mW.Quests.AddNew();
                qW.NextAliasID = 0;                                                 // isolate the alias findings
                var explicitVoice = FormKey.Factory("02F7C3:Skyrim.esm");           // a real (non-null) voice-type link — value irrelevant, must be kept
                var al = new QuestAlias { ID = 0, Flags = QuestAlias.Flag.Optional };
                al.VoiceTypes.SetTo(explicitVoice);
                qW.Aliases.Add(al);
                var wfills = DialogueCkParity.ApplyQuestDefaults(qW);
                bool flagsKept = qW.Aliases[0].Flags == QuestAlias.Flag.Optional;
                bool voiceKept = qW.Aliases[0].VoiceTypes.FormKeyNullable == explicitVoice;
                bool noAliasFill = !wfills.Any(f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal));
                Check(flagsKept && voiceKept && noAliasFill,
                    $"QUST-ALIAS-WINS explicit alias Flags=Optional + VoiceTypes kept, no fill — flags={qW.Aliases[0].Flags} voiceKept={voiceKept} noFill={noAliasFill}");
            }

            // ---- QUST-ALIAS-LOCATION: a LOCATION-type alias gets FNAM (Flags=0) but NOT VTCK — VTCK is scoped to
            //      reference aliases (voice types are an actor concept; a location alias resolves to a place). Both the
            //      fill and MissingQuestDefaults must honour the scope (no VTCK fill, no VTCK gap) so no false-warn on a
            //      CK-authored location alias. ----
            {
                var mL = new SkyrimMod(new ModKey("HcCkpAliasLoc", ModType.Master), SkyrimRelease.SkyrimSE);
                var qL = mL.Quests.AddNew();
                qL.NextAliasID = 0;                                                             // isolate the alias findings
                qL.Aliases.Add(new QuestAlias { ID = 0, Type = QuestAlias.TypeEnum.Location });  // Flags + VoiceTypes null
                var beforeL = DialogueCkParity.MissingQuestDefaults(qL);
                bool locFnamGap = beforeL.Any(g => g.Subrecord.Contains("FNAM", StringComparison.OrdinalIgnoreCase) && g.Subrecord.Contains("Aliases[0]", StringComparison.Ordinal));
                bool noLocVtckGap = !beforeL.Any(g => g.Subrecord.Contains("VTCK", StringComparison.OrdinalIgnoreCase));
                var lfills = DialogueCkParity.ApplyQuestDefaults(qL);
                bool flagsFilled = qL.Aliases[0].Flags == default(QuestAlias.Flag);
                bool voiceNotFilled = qL.Aliases[0].VoiceTypes.FormKeyNullable is null;
                bool noVtckFill = !lfills.Any(f => f.Label.Contains("VTCK", StringComparison.OrdinalIgnoreCase));
                int afterL = DialogueCkParity.MissingQuestDefaults(qL).Count;
                Check(locFnamGap && noLocVtckGap && flagsFilled && voiceNotFilled && noVtckFill && afterL == 0,
                    $"QUST-ALIAS-LOCATION location alias → FNAM filled, VTCK NOT (scoped to reference) — fnamGap={locFnamGap} noVtckGap={noLocVtckGap} flags={flagsFilled} voiceUnset={voiceNotFilled} noVtckFill={noVtckFill} after={afterL}");
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

    /// <summary>Read a created DialogBranch's CK-parity fields back off the written patch: (Category/TNAM, Flags/DNAM).
    /// Each is null when its subrecord is ABSENT (Mutagen omits it when unset), else the value the bytes carry. The
    /// null-vs-zero distinction is load-bearing for the DNAM arms: an absent DNAM and a present DNAM=0 are DIFFERENT
    /// records that the engine reads differently (absent → TopLevel; 0 → no flags, #212), so a Flags of null and a
    /// Flags of (Flag)0 must never be conflated — which is exactly why this returns the nullable, not a raw value.</summary>
    static (DialogBranch.CategoryType? category, DialogBranch.Flag? flags) ReadBranch(string patchPath, FormKey branchFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var br = ov.DialogBranches.FirstOrDefault(x => x.FormKey == branchFk);
            return (br?.Category, br?.Flags);
        }
        catch { return (null, null); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Read a created DialogTopic's Priority (PNAM) back off the written patch — null only when the record
    /// can't be found (Priority is a non-nullable float, so a found topic always yields one).</summary>
    static float? ReadTopicPriority(string patchPath, FormKey topicFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return ov.DialogTopics.FirstOrDefault(x => x.FormKey == topicFk)?.Priority;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Read a created Quest's CK-parity fields back off the written patch: (NextAliasID?, objective count).
    /// NextAliasID is null when the ANAM subrecord is absent (Mutagen omits it when unset).</summary>
    static (uint? nextAliasId, int objectiveCount) ReadQuest(string patchPath, FormKey questFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var q = ov.Quests.FirstOrDefault(x => x.FormKey == questFk);
            if (q is null) return (null, 0);
            return (q.NextAliasID, q.Objectives.Count);
        }
        catch { return (null, 0); }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}

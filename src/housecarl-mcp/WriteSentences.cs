using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The load-bearing phrases a shared sentence must still contain; a phrase is the CLAIM, not the wording.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class MustStateAttribute : Attribute
{
    internal string[] Phrases { get; }
    internal MustStateAttribute(params string[] phrases) => Phrases = phrases;
}

/// <summary>The declared way to say a shared sentence carries no claim worth pinning; the reason is never parsed.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class NoClaimsAttribute : Attribute
{
    internal string Reason { get; }
    internal NoClaimsAttribute(string reason) => Reason = reason;
}

/// <summary>One source per sentence for the write surface's user-facing prose: every outcome renders twice, text and json.
/// Contracts in docs/architecture/write-path.md.</summary>
internal static class WriteSentences
{
    // ---- budgets -------------------------------------------------------------------------------------
    /// <summary>The char budget a WRITE render works to: the caller's max_chars, or the server default; the read surface keeps its own.</summary>
    internal static int Cap(int maxChars) => maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

    /// <summary>The same for a READ-BACK dump, bounded well below Cap so the truncation note itself reaches the caller.</summary>
    internal static int ReadbackCap(int maxChars) => maxChars > 0 ? maxChars : Wire.ReadbackMaxChars;

    // ---- the epoch stamp -----------------------------------------------------------------------------
    /// <summary>The index build a write resolved winners against, appended to every write render; contract in docs/architecture/write-path.md.</summary>
    internal static string Epoch(OrderStamp? stamp) => Wire.EpochLine(stamp);

    /// <summary>The reading that says a record is not in the file this call just wrote, with no remedy on it.</summary>
    [MustState("does not contain this record")]
    internal const string RecordAbsentReading =
        "the written file was re-opened and does not contain this record";

    [MustState("does not contain this record", "this edit is not in it")]
    internal const string RecordAbsentFromWrittenFile =
        RecordAbsentReading + ", so this edit is not in it. "
        + "Re-read the record and re-issue the edit; if it reports the same thing again, capture this response in a bug report";

    internal static string CreateRecordAbsentFromWrittenFile(string readBackCall) =>
        RecordAbsentReading + ", so this record is not in it. "
        + $"Confirm with {readBackCall}, which reads the artifact off disk; create it again ONLY if that agrees it is "
        + "absent. On this evidence alone, do NOT re-issue the create: " + Twins.CreateReissueCost
        + ". If the read disagrees with this response, capture both in a bug report";

    internal static string OpaqueLeafCaveat(int bytes) =>
        $"  [re-read as {bytes} opaque byte(s) only, structure NOT checked — a blob's layout follows its record's FormVersion]";

    // ---- artifact headers (text lane; json states these as typed fields) -----------------------------
    [MustState("your ORIGINAL file was rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceRewritten = "your ORIGINAL file was rewritten; " + NoBackupOrUndo;

    [MustState("your ORIGINAL file rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceWouldRewrite = "your ORIGINAL file rewritten; " + NoBackupOrUndo;

    [MustState("no houseCARL backup or undo")]
    internal const string NoBackupOrUndo = "no houseCARL backup or undo";

    // ---- the runtime-config reminder (merge + compact) -----------------------------------------------
    // Each states only what THIS operation did, and never another runtime system's addressing grammar.

    [MustState("Nothing here rewrites those files", "no longer describes those records", "stops loading at the swap")]
    internal const string MergeRuntimeConfigs =
        "Nothing here rewrites those files. After the swap a donor's records live under the merged plugin's name, so a " +
        "line that names a donor — whether to address a record in it or to filter on it — no longer describes those " +
        "records. Separately, a SkyPatcher config file whose NAME is a donor's filename (Plugin.esp.ini) is read only " +
        "while that plugin is active, so it stops loading at the swap: every line in it, including the lines that never " +
        "name a donor.\n";

    [MustState("does not read", "nothing here rewrites them", "no longer reaches that record")]
    internal const string CompactRuntimeConfigs =
        "That pass reads plugins; it does not read runtime config files (SPID, KID, SkyPatcher, Open Animation " +
        "Replacer), and nothing here rewrites them. The plugin name is unchanged, but a line in one of them that " +
        "addresses a record by an object id this compaction moved no longer reaches that record once the compacted " +
        "plugin is the one loading.\n";

    internal static string InPlaceModFolder(string modFolder) =>
        $"mod folder: {modFolder}  — already active in your load order; re-sort only if a winner changed\n";

    internal static string UnscannablePlugin(RemapEngine.UnscannablePlugin p) => p.Cause switch
    {
        RemapEngine.UnscannableCause.Unopenable =>
            $"{p.Plugin} — could not be OPENED, probably held open by another program; close xEdit, MO2 or Skyrim and run this again ({p.Reason})",
        _ => $"could not fully read {p.Plugin}: {p.Reason}",
    };

    internal static string NewOrExtendedArtifact(bool extended, string file, long bytes, string modFolder) =>
        (extended ? $"extended {file} (existing patch grown; {bytes} bytes)\n"
                  : $"wrote {file} (new patch; {bytes} bytes)\n")
      + (extended ? $"mod folder: {modFolder}\n"
                  : $"mod folder: {modFolder}  — enable it in MO2 to use the patch; MO2 adds a newly activated plugin at the END of the load order, so it overrides what is already there\n");

    internal static string Masters(IReadOnlyList<string> masters) =>
        $"masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}\n";

    // ---- closure copy --------------------------------------------------------------------------------
    [MustState("is NOT a master")]
    internal const string CopyStandalone = "standalone: the source is NOT a master of this patch.";

    [MustState("IS among the masters", "NOT standalone")]
    internal const string CopySourceMastered =
        "!! the source IS among the masters — this copy is NOT standalone. Nothing was silently fixed; inspect the patch before relying on it.";

    [MustState("base-game master", "appearance transplant", "not a standalone-ization")]
    internal const string CopySourceBaseGame =
        "note: the source is defined in a base-game master (always loaded) — nothing is being \"removed\", so links " +
        "to it are kept and mastered normally; this copy is an appearance transplant, not a standalone-ization.";

    [MustState("NOT VERIFIED", "do NOT re-run")]
    internal const string CopyReadBackUnverified =
        "masters: <NOT VERIFIED — the post-write read-back failed>\n" +
        "the patch WAS written, so do NOT re-run blindly (that mints a duplicate); read its records back with " + ToolNames.Records +
        " source=\"<the patch>.esp\" types=[\"NPC_\"]. The MASTERS line above stays unverified either way — no houseCARL tool " +
        "lists a plugin's masters; check them in xEdit or the CK.";

    [MustState("re-author")]
    internal const string CopyStripConsequence =
        "  the clone keeps what was copied and NOT the source's own references above — re-author those against your own or vanilla records as needed.";

    [NoClaims("a labelled list header; the claim is in the per-cycle lines it introduces")]
    internal const string CopyCyclesHeader = "cycles found while walking (recorded, not an error):";

    [NoClaims("a list header; every claim it introduces is per-record and rendered beside the record")]
    internal const string CopyInternalizedHeader = "internalized under new FormIDs (EditorIDs preserved):";

    // ---- the ordered source universe: parameterised sentences, split so the checks can reach them ----------
    // [MustState] is field-only, so each sentence here is an invariant const the render emits verbatim.

    [NoClaims("a label; the claim is the source name it introduces")]
    internal const string CopySourceSingleLabel = "source: ";

    [MustState("in order", "first hit wins")]
    internal const string CopySourceListLabel = "sources (in order, first hit wins): ";

    [MustState("no source produced")]
    internal const string CopySourceMissLead = "no source produced ";

    [MustState("Consulted, in order:")]
    internal const string CopySourceMissConsulted = ". Consulted, in order: ";

    [MustState("from_source=", "'winner'", "dangling link", "'Type:refuse'")]
    internal const string CopySourceMissRemedy =
        ". Name the plugin that defines it in from_source=, or 'winner' for the active load order's winning version. " +
        "If the record exists NOWHERE — a dangling link, which is the typical cause — no source will produce it: " +
        "exclude its record type instead, with 'Type:refuse' to stop the copy at it, or 'Type:stop' to prune the " +
        "link and keep it (which needs its plugin in your active load order).";

    [MustState("could not be read")]
    internal const string CopySourceFaultLead = " but it could not be read — ";

    [MustState("not a missing record", "from_source=")]
    internal const string CopySourceFaultRemedy =
        ". This is not a missing record: adding another source will not help. Repair or replace that plugin, or " +
        "name a different one in from_source=.";

    [MustState("MO2 mod folder")]
    internal const string CopyArmFolderLead = "MO2 mod folder ";

    [MustState("active load order")]
    internal const string CopyArmActive = "from the active load order";

    [MustState("overwrite folder")]
    internal const string CopyArmOverwriteLayer = "MO2's overwrite folder, ";

    [MustState("game's Data folder")]
    internal const string CopyArmDataLayer = "the game's Data folder, ";

    [MustState("read straight off disk", "no MO2 mod folder", "source_provider=")]
    internal const string CopyArmNoFolder =
        "read straight off disk, outside your mods, overwrite and Data folders — no MO2 mod folder to pass as " +
        "source_provider= for its files";

    [MustState("RESERVED", "source_provider=", "Rename that mod folder")]
    internal const string CopyArmReservedFolderTail =
        " — but that name is RESERVED for MO2's overwrite layer and the game's Data folder, so it cannot be passed " +
        "as source_provider=: a placement given it reads the layer, not this mod. Rename that mod folder in MO2";

    internal static string CopyArm(SourceArmRef arm) => $"{arm.Spelling} ({CopyArmWhere(arm)})";

    /// <summary>Where one source arm resolved. The layer name is DOUBLE-quoted, the delimiter chosen because a mod
    /// folder name can hold an apostrophe or parentheses and this token is copied into the next call's source_provider=.</summary>
    internal static string CopyArmWhere(SourceArmRef arm)
    {
        var active = arm.Kind == SourceArmKind.ActiveOrder ? CopyArmActive : null;
        if (arm.Layer is not { } layer) return active ?? CopyArmNoFolder;
        var where = layer.Kind switch
        {
            SourceLayerKind.Overwrite => $"{CopyArmOverwriteLayer}\"{layer.Name}\"",
            SourceLayerKind.GameData => $"{CopyArmDataLayer}\"{layer.Name}\"",
            _ => $"{CopyArmFolderLead}\"{layer.Name}\""
                 + (AssetResolver.IsReservedLayerName(layer.Name) ? CopyArmReservedFolderTail : ""),
        };
        return active is null ? where : $"{active}, {where}";
    }

    internal static string CopySourcesConsulted(IReadOnlyList<SourceArmRef> sources) =>
        sources.Count == 1
            ? $"{CopySourceSingleLabel}{CopyArm(sources[0])}\n"
            : $"{CopySourceListLabel}{string.Join(" -> ", sources.Select(CopyArm))}\n";

    internal static string CopySourceMiss(string what, IReadOnlyList<SourceArmRef> sources) =>
        CopySourceMissLead + what + CopySourceMissConsulted + string.Join(", ", sources.Select(CopyArm)) + CopySourceMissRemedy;

    internal static string CopySourceFault(string what, SourceArmRef? arm, string cause) =>
        $"'{arm?.Spelling ?? "a source"}'{(arm is { } a ? $" ({CopyArmWhere(a)})" : "")} carries {what}"
        + CopySourceFaultLead + cause + CopySourceFaultRemedy;

    // ---- the seed-shape boundary ---------------------------------------------------------------------
    [MustState("seed_paths takes a record link or a list of record links", ToolNames.Apply)]
    internal const string CopySeedShapeRoute =
        " — seed_paths takes a record link or a list of record links, and nothing else. Copying a field whose " +
        "entries carry links INSIDE them is a field-bundle copy: use " + ToolNames.Apply + "'s bundle=/assignments= zip, " +
        "which transplants each named field WHOLE onto the target — one CopyFrom per named field per target, replacing what the " +
        "target had rather than merging into it. Nothing was written.";

    [MustState("was already in this patch", "this call did not create it", "enable the mod", "a NEW patch")]
    internal const string CopyPatchOffOrderRoute =
        " is not in your active load order, so this patch cannot be written at all until that link resolves. The " +
        "reference was already in this patch before this call — this call did not create it, and no exclude_types " +
        "setting affects it. Either enable the mod that provides that plugin, or write to a NEW patch instead of " +
        "extending this one with into=. Nothing was written.";

    [MustState("this copy carried it across", "seed_paths=", "enable the mod")]
    internal const string CopyCopiedOffOrderRoute =
        " is not in your active load order, so this patch cannot be written at all. The link sits on a field " +
        "seed_paths= never named, so the walk never reached that record to copy it and this copy carried it across " +
        "as a link instead. Either name that field in seed_paths= so the record is internalized too, or enable the " +
        "mod that provides the plugin so the link can be mastered normally. Nothing was written.";

    [MustState("the TARGET's", "not the seed path", "target=")]
    internal const string CopyUnwritableTargetRoute =
        " — the seed path is fine; it is the TARGET's property that cannot be written, not the seed path. Copy onto " +
        "a record of the same type that carries its own, or mint a clone with new_editorid= instead of target=. " +
        "Nothing was written.";

    [MustState("carry no record links", "TEMPLATED", "the template it points at")]
    internal const string CopyNoSeeds =
        "the seed fields carry no record links, so this copy would produce nothing. The usual cause is a TEMPLATED " +
        "record — one whose template flags hand these fields to another record, leaving its own empty by design; " +
        "copy the template it points at instead. Otherwise check seed_paths= against the record.";

    [MustState("the ENTIRE property was cleared", "not only the link")]
    internal const string CopyStripWholeProperty =
        "   <- the ENTIRE property was cleared to remove this, not only the link(s) named — everything else it carried is gone too.";

    [MustState("CLEARED", "not a mixture")]
    internal const string CopySeedClearedNote =
        "  (the source carries none, so the target's was CLEARED — the result is the source's, not a mixture)";

    // ---- kept links, told apart ----------------------------------------------------------------------
    [MustState("outside the source", "mastered normally")]
    internal const string CopyKeptOutside = "link(s) resolve outside the source — mastered normally.";

    [MustState("still point INTO the source", "not standalone")]
    internal const string CopyKeptExcluded =
        "link(s) were pruned by exclude_types and still point INTO the source — this artifact is not standalone " +
        "for them, and masters the plugin they live in.";

    [MustState("exclude_types", "this call wrote it")]
    internal const string CopyLeakFromExclusion =
        " was pruned by exclude_types and then attached to the target unmapped — this call wrote it, so the target " +
        "is not where to look. Either drop that exclusion so the record is internalized, or copy a field set that " +
        "does not reach it.";

    [MustState("master a plugin the game does not load", "'Type:refuse'", "enable the mod")]
    internal const string CopyStopOffOrderRoute =
        " is not in your active load order, and 'Type:stop' KEEPS the link — so this patch would have to master a " +
        "plugin the game does not load, which cannot be written at all. Either use 'Type:refuse' for that type, so " +
        "the copy stops there and tells you, or enable the mod so the link can be mastered normally. Nothing was written.";

    [MustState("NESTED group", "target=")]
    internal const string CopyTargetShapeRoute =
        " lives in a NESTED group (a placed reference, a cell, a dialog response), which target= cannot override. " +
        "Name a top-level record — for an NPC's appearance that is the NPC_ itself, not a reference placed in a cell.";

    // ---- the from record's own provenance ------------------------------------------------------------
    [MustState("read from")]
    internal const string CopyFromArmLead = "the source record was read from ";

    internal static string CopySourceOffOrderFolder(string folder, ModFolderStanding standing) =>
        $"note: '{folder}' is {(standing == ModFolderStanding.Unregistered ? UnregisteredModFolder : OffOrderModFolder)}.";

    // ---- the copied records' asset paths -------------------------------------------------------------
    [MustState("does NOT place them", ToolNames.Place)]
    internal const string CopyAssetPathsHeader =
        "asset paths the copied records reference (this call does NOT place them — check each with " +
        ToolNames.AssetStatus + ", then place what you keep with " + ToolNames.Place + "; a path only the mod you " +
        "read FROM provides reads as absent in asset_status if MO2 does not load that mod, and is still placed by " +
        "naming it in source_provider=):";

    // ---- dry run -------------------------------------------------------------------------------------
    [MustState("NOTHING was written", "originals untouched")]
    internal const string DryRunHeader =
        "DRY RUN — validated only; NOTHING was written (no patch file, no mod folder, originals untouched).\n";

    internal static string DryRunWouldWrite(bool inPlace, bool extended, string file, string inPlaceVerb) =>
        inPlace  ? $"the real call would {inPlaceVerb} {file} IN PLACE — {InPlaceWouldRewrite}.\n"
      : extended ? $"the real call would EXTEND the existing patch {file}.\n"
                 : $"the real call would write a NEW patch {file} (name preview — the real write re-picks a free name, "
                 +  "so the auto-suffix can shift if another patch lands first).\n";

    internal static string DryRunMasters(IReadOnlyList<string> masters) =>
        $"expected masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}"
      + "  [derived from the would-be content; the real write derives its own lean header]\n";

    internal static string DryRunClose(string proved, string realVerb) =>
        $"{proved}; to {realVerb} for real, repeat the call without dry_run. "
      + "(A real write can still fail at serialize/commit — disk faults and data Mutagen refuses to serialize surface only there.)";

    // ---- row-truncation notes ------------------------------------------------------------------------
    internal static string RowsCutOperationIntact(bool dryRun, string pastParticiple, bool someDidNotLand = false) =>
        dryRun ? "the dry run covered every one"
        : someDidNotLand ? "every one was attempted, and the ones that did NOT land are named outside this cut"
        : $"every one WAS {pastParticiple}";

    /// <summary>How many absent records a response names before counting the rest; the list is hoisted out of the row budget.</summary>
    internal const int AbsentRecordsShown = 10;

    internal static string AbsentRecordList(IReadOnlyList<string> tokens) =>
        tokens.Count <= AbsentRecordsShown
            ? "Record(s): " + string.Join(", ", tokens)
            : "Record(s): " + string.Join(", ", tokens.Take(AbsentRecordsShown))
              + $", and {tokens.Count - AbsentRecordsShown} more";

    internal static string JsonRowsCut(int cap) =>
        $"the render hit max_chars={cap} and dropped trailing rows";

    internal static string CreateRowsCutRemedy(string readBackCall, bool someDidNotLand = false) =>
        $"{RowsCutOperationIntact(false, "created", someDidNotLand)}. Read them back with {readBackCall} — {Twins.CreateReissueTrap}";

    // ---- post-write report blocks (create) -----------------------------------------------------------
    internal static string CheckCouldNotRun(string checkName, string error, string createdNoun, string verifyManually) =>
        $"  {checkName} check could not run: {error} — {createdNoun} WERE created; {verifyManually}\n";

    internal static string ScanIncomplete(string absentThing) =>
        $"  note: a BSA or a loose mod folder failed to read this scan, so {absentThing} above may merely be unscanned — verify in MO2.\n";

    [MustState("Creation-Kit work this response was supposed to name")]
    internal const string CellRowsCutLoss =
        "each dropped row is Creation-Kit work this response was supposed to name";

    // ---- place_asset: naming which copy to read ------------------------------------------------------
    [MustState("will not guess which copy is correct")]
    internal const string PlaceSourceWillNotGuess = "houseCARL will not guess which copy is correct";

    internal static string PlaceSourceAmbiguous(string rel, IReadOnlyList<string> providerNames) =>
        $"{providerNames.Count} providers supply '{rel}' — {PlaceSourceWillNotGuess}. "
      + $"Pass source_provider= one of these names, quotes excluded: {string.Join("; ", providerNames)}"
      + $", or source_provider={AssetSourceChoice.WinnerToken} for whichever copy currently wins the VFS.";

    [MustState("nothing was substituted", "still supply it")]
    internal const string PlaceSourceNoSubstitute =
        "nothing was substituted for it — the providers below still supply it, and one of them is what you meant if the name is a typo";

    [MustState("winner pole is spelled")]
    internal const string PlaceSourcePoleSpelling =
        "(the winner pole is spelled " + AssetSourceChoice.WinnerToken + " — a bare name always means a provider of that name)";

    [MustState("NOT currently loading", "Naming it is what reaches it")]
    internal const string PlaceSourceNameReachesUnticked =
        "Naming a mod folder MO2 is NOT currently loading reads that mod's own copy off disk — the loose file, then "
      + "that folder's own archives — and the result says so. Naming it is what reaches it: with source_provider= "
      + "omitted, only the mods MO2 loads are considered. For a mod MO2 IS loading, nothing changes: its loose files "
      + "are reached by the mod's name and a file inside its archive by that archive's filename, as before.";

    [MustState("MO2 mod folder of that name")]
    internal const string PlaceSourceDiskFolderSearched =
        "houseCARL also looked in the MO2 mod folder of that name, and read neither a loose copy there nor one inside "
      + "that folder's own archives";

    [MustState("may merely be unscanned")]
    internal const string PlaceSourceScanIncomplete =
        "NOTE: a BSA or a loose mod folder failed to read this build, so it may merely be unscanned — any loose folder "
      + "that failed is named among this response's root read failures, and any archive that failed is named by "
      + ToolNames.AssetStatus + ".";

    [MustState("no MO2 mod folder of that name")]
    internal const string PlaceSourceNoSuchFolder =
        "houseCARL also looked under MO2's mods folder, and there is no MO2 mod folder of that name";

    [MustState("named, never pathed")]
    internal const string PlaceSourceNotAFolderName =
        "a provider is named, never pathed — this carries a separator, a drive, a '..' or a trailing dot or space, so "
      + "it is not a name houseCARL looks for on disk at all";

    [MustState("could not be read")]
    internal const string PlaceSourceFolderUnreadable =
        "houseCARL also looked in the MO2 mod folder of that name, and something there could not be read";

    [MustState("active load order already provides")]
    internal const string PlaceSourceReservedName =
        "that name is one the active load order already provides files under, and it names a layer rather than a mod folder";

    internal static string PlaceSourceNamedAbsent(string provider, string rel, IReadOnlyList<string> providerNames,
                                                  OffOrderReason reason, string? unreadableName = null,
                                                  string? unreadableCause = null, string? pathHint = null,
                                                  bool scanIncomplete = false) =>
        $"'{provider}' does not supply '{rel}'"
        // ONE sentence per reason, and a switch expression with NO default arm: CS8509 makes a new outcome a build diagnostic.
      + reason switch
        {
            OffOrderReason.NotConsulted     => ". ",          // nothing on disk was looked at; claim nothing about it
            OffOrderReason.Found            => ". ",          // unreachable from a refusal, and silent if it ever is
            OffOrderReason.ReservedName     => $" — {PlaceSourceReservedName}. ",
            OffOrderReason.NotAFolderName   => $" — {PlaceSourceNotAFolderName}. ",
            OffOrderReason.NoSuchFolder     => $" — {PlaceSourceNoSuchFolder}. ",
            OffOrderReason.NoCopyInFolder   => $" — {PlaceSourceDiskFolderSearched}. ",
            OffOrderReason.FolderUnreadable => $" — {PlaceSourceFolderUnreadable}. ",
        }
      + (unreadableName is null ? "" : $"NOTE: '{unreadableName}' could not be read ({unreadableCause}), so this may be unscanned rather than absent. ")
      + (scanIncomplete ? PlaceSourceScanIncomplete + " " : "")
      + (pathHint is null ? "" : pathHint + " ")
      + (providerNames.Count > 0
            ? $"{PlaceSourceNoSubstitute} — pass one of these names instead, quotes excluded: "
            + $"{string.Join("; ", providerNames)}. "
            : "")
      + PlaceSourcePoleSpelling;

    [MustState("NOT enabled in MO2")]
    internal const string OffOrderModFolder =
        "a mod folder that is NOT enabled in MO2 — you named it, so houseCARL read it off disk";

    [MustState("NOT registered", "refresh MO2")]
    internal const string UnregisteredModFolder =
        "a mod folder MO2 has NOT registered — nothing in MO2's list to switch on; refresh MO2 to pick it up. You "
        + "named the folder, so houseCARL read it off disk";

    internal static string PlaceSourceOffOrder(string provider, bool ownerEnabled = false) => ownerEnabled
        ? $"read from '{provider}', out of a root archive the engine does NOT load (no active plugin binds it) — you "
        + "named the mod, so houseCARL looked inside its own archives; the bytes are that mod's, and nothing about "
        + "that archive has to change for the copy just placed"
        : $"read from '{provider}', {OffOrderModFolder}; the bytes are that mod's, and enabling it is not required "
        + "for the copy just placed";

    [MustState("only when source= is omitted")]
    internal const string PlaceBothSlotsPoleConstraint =
        "source_provider= works here only when source= is omitted — it names WHOSE copy, not which file, and two slots are two files";

    [MustState("only applies to a Data-relative source", "already names one exact copy")]
    internal const string PlaceSourceProviderNeedsRelPath =
        "source_provider= only applies to a Data-relative source resolved through the VFS. The source you passed is an "
      + "on-disk path, which already names one exact copy — drop source_provider=, or pass source= the Data-relative path.";

    // ---- the into=-extend refusals -------------------------------------------------------------------
    [MustState("will not create a patch here", "only drops a record the patch ITSELF already carries")]
    internal const string RemoveNoFreshPatch =
        "houseCARL will not create a patch here, since a removal only drops a record the patch ITSELF already carries";


    /// <summary>Sentences the SAME outcome must carry on BOTH transports, as whole invariant strings.</summary>
    internal static class Twins
    {
        // ---- create's post-write hazard reports ------------------------------------------------------
        [MustState("plays SILENT in game")]
        internal const string VoiceStake = "a created voiced response with NO .fuz plays SILENT in game";

        [MustState("runs NOTHING in game")]
        internal const string ScriptStake = "a bound script that is unwired or uncompiled runs NOTHING in game";

        [MustState("houseCARL does not author world content")]
        internal const string CellStake =
            "a created cell is a valid, correctly-placed record but EMPTY — houseCARL does not author world content";

        [MustState("does NOT check grid-occupancy", "engine behavior undefined", "OVERRIDE it instead of creating a new one")]
        internal const string GridOccupancy =
            "houseCARL does NOT check grid-occupancy — a NEW exterior cell at a grid your load order already fills "
          + "collides (engine behavior undefined). To change an existing cell, OVERRIDE it instead of creating a new one.";

        [MustState("allocates the records AGAIN", "second full patch", "prior contents discarded")]
        internal const string CreateReissueCost =
            "a repeated create allocates the records AGAIN (on the default lane patch= auto-suffixes into a second "
          + "full patch; under into= each record is re-created at its old FormID with its prior contents discarded)";

        [MustState("do NOT re-issue this call", "second full patch", "prior contents discarded")]
        internal const string CreateReissueTrap =
            "do NOT re-issue this call to see the rest: " + CreateReissueCost;

        [MustState("Do NOT re-issue the create", "compare rendered vs total")]
        internal const string ReportBlockCut =
            "the records WERE created and this block is only a render of them — compare rendered vs total rather than "
          + "reading the list as the whole answer. Do NOT re-issue the create to widen it: that allocates the records again";

        // ---- write_seq -------------------------------------------------------------------------------
        [MustState("NOTHING was written")]
        internal const string SeqUnchanged =
            "the destination already held EXACTLY these bytes, so NOTHING was written";

        [MustState("nothing was lost")]
        internal const string SeqReplacedSameBytes =
            "the file already at that path held EXACTLY these bytes; it was rewritten only because its timestamp "
          + "could not be refreshed in place, so nothing was lost.";

        [MustState("keeps no backup")]
        internal const string SeqReplacedUserFolder =
            "a .seq was already at that path and has been OVERWRITTEN; houseCARL keeps no backup, and in a folder "
          + "you named that file may have been the mod's own shipped .seq.";

        [MustState("was overwritten", "houseCARL's own earlier output")]
        internal const string SeqReplacedOwnFolder =
            "the previous .seq in that houseCARL folder was overwritten (houseCARL's own earlier output — the "
          + "ordinary regenerate case).";

        [MustState("has been stamped forward", "contents untouched", ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds=", "no longer reads")]
        internal const string SeqTimestampRefreshed =
            "its mtime was older than the plugin and has been stamped forward (contents untouched); "
          + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds= compares those two mtimes in its SEQ staleness check, so this file "
          + "no longer reads as stale — for the copy the load order actually serves, which is this one only if this "
          + "folder wins the SEQ\\ conflict.";

        [MustState("NOTHING was written", "lists only quests with the Start Game Enabled flag")]
        internal const string SeqNoQuests =
            "a .seq lists only quests with the Start Game Enabled flag, so none is needed and NOTHING was written";

        [MustState("consulted no load-order build", "not a dropped field")]
        internal const string SeqNoEpoch =
            "a .seq is derived from the plugin FILE alone (its FormID encoding is load-order-independent), so this "
          + "call consulted no load-order build — the absent stamp is a fact, not a dropped field.";

        [MustState("does not verify", "use " + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds=")]
        internal const string SeqStandingLimit =
            "this makes the quest(s) START at game start; it does not verify the quest or its dialogue is otherwise "
          + "well-formed (use " + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds= for the dialogue "
          + "graph — that family validates the topics and quests you name and will not sweep the whole order).";

        [MustState("nothing is missing from the FILE", "writes the .seq again")]
        internal const string SeqListCutRemedy =
            "the .seq itself carries ALL of them — nothing is missing from the FILE. Re-run only if you need this LIST "
          + "widened: for a plugin OUTSIDE a houseCARL folder with no lane named, that writes the .seq again into "
          + "ANOTHER fresh mod folder (name into=/out_path=, or let a plugin in its own houseCARL folder default "
          + "there — at any named destination a byte-identical .seq is left untouched)";
    }
}

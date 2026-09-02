using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

// ======================================================================
//  NpcAppearanceCopy — the record half of the composed standalone-NPC-copy verb
//  (capability chain Stage 3; STANDALONE_NPC_COPY_CAPABILITY_CHAIN_2026-07-02 §1).
//
//  THE MECHANISM: Mutagen's public record.Duplicate(newKey) + RemapLinks — the SAME
//  blessed deep-copy the RemapEngine pins (remap-wave1-mech), NOT a field-by-field
//  re-authoring. That choice is load-bearing: the 2026-07-01 build test proved two
//  appearance facts empirically (HDPT.Parts morph-.tri refs are load-bearing for
//  lip-sync; TextureLighting defaults to 0 = dark skin on fresh creates), and BOTH
//  traps are structurally impossible under a whole-record copy — Duplicate carries
//  every field, so there is no field for a future session to forget.
//
//  THE INTERNALIZE RULE (what gets deep-copied vs what stays a link):
//    a linked record is INTERNALIZED (duplicated into the patch under a new key) iff
//      • its defining plugin IS the donor's plugin (the plugin being standalone-ized
//        away from — even if currently active), OR
//      • it does not resolve in the ACTIVE load order (it would be a missing master).
//    Every other link (vanilla, active shared-resource mods) stays a link — the patch
//    masters it normally. Headpart EditorIDs are PRESERVED on the copies: the engine
//    maps facegeom shape names → headparts BY NAME (build-test-pinned), so renaming
//    them would silently regenerate a vanilla head.
//
//  CLONE-MODE STRIP (Q3 — a standalone clone must be donor-free, loudly):
//    after internalize + remap, any link on the clone still pointing into the donor
//    (or at an unresolvable key) is NON-appearance (factions, outfits, packages,
//    VMAD…). Those are STRIPPED — nullable link → null; list entry → removed;
//    nullable link-bearing substruct → null — and EVERY strip is reported by field
//    name. A non-strippable (required, non-list) foreign link is a LOUD refusal,
//    never a silent keep (it would drag the donor back in as a master).
// ======================================================================

/// <summary>One internalized record for the report: what it is, where it came from, where it landed — EditorID
/// included because headpart EditorIDs are load-bearing (facegeom block-name identity) and the report should show
/// they were preserved.</summary>
public sealed record InternalizedRecord(string Type, string EditorId, FormKey OldKey, FormKey NewKey, string PulledBy);

/// <summary>The composed copy's outcome — everything the tool render needs, or a loud refusal with nothing written.
/// <see cref="Warning"/> is a POST-COMMIT caveat: the patch WAS written but a follow-up step (read-back) failed —
/// never conflated with a refusal (that mislabel would send the user re-running against a live patch).
/// <see cref="Reused"/> = extend-lane dedupe: donor records a PRIOR run already internalized into this patch
/// (matched by type + preserved EditorID) are re-linked, not re-copied — a second copy would put two same-named
/// headparts in one plugin and break the facegeom block-name mapping. <see cref="HarvestedAssetPaths"/> is
/// collected from the IN-PATCH duplicates BEFORE serialize (overlay-backed donor bodies may be released by then).
/// <see cref="DonorIsBaseGame"/> = the donor is defined in a base-game/implicit master: nothing is being
/// "removed", so no records are donor-bound — the copy is an override-style transplant, said plainly.</summary>
public sealed record NpcCopyOutcome(
    bool Success, string? Error,
    string Mode,                                          // "apply" | "clone"
    FormKey DonorKey, string DonorReadFrom, bool DonorOutOfLoadOrder,
    FormKey NewNpcKey,                                    // apply: the target's key; clone: the clone's new key
    string? OutPath, bool Extended,
    IReadOnlyList<InternalizedRecord> Internalized,
    IReadOnlyList<string> Reused,
    int KeptLinkCount,
    IReadOnlyList<string> CopiedFields,                   // apply mode
    IReadOnlyList<NpcAppearanceCopy.StripReport> Stripped, // clone mode
    IReadOnlyList<string> Masters, bool DonorAmongMasters, bool DonorIsBaseGame,
    IReadOnlyList<string> HarvestedAssetPaths,
    NpcAssetOutcome? Assets, long Bytes, string? Warning)
{
    public static NpcCopyOutcome Fail(string error) => new(
        false, error, "", default, "", false, default, null, false,
        Array.Empty<InternalizedRecord>(), Array.Empty<string>(), 0, Array.Empty<string>(),
        Array.Empty<NpcAppearanceCopy.StripReport>(), Array.Empty<string>(), false, false,
        Array.Empty<string>(), null, 0, null);
}

public static class NpcAppearanceCopy
{
    /// <summary>Named cap on the appearance closure walk. A real appearance subtree is small (the build test:
    /// 8 records); a walk that blows past this is a runaway (a donor-internal custom race pulling skeletons,
    /// body mods, …) and is REFUSED LOUD with the chain of what pulled what — never silently truncated (Q3).</summary>
    public const int ClosureCap = 128;

    /// <summary>Fetch a record body from the DONOR's universe by FormKey (the donor file's own version in the
    /// out-of-load-order lane; the load-order winner in the active lane). Null = the donor universe doesn't have it.</summary>
    public delegate IMajorRecordGetter? DonorFetch(FormKey fk);

    /// <summary>Does this FormKey resolve in the ACTIVE load order? (If yes and it isn't donor-defined, the
    /// patch can simply master it — no internalize needed.)</summary>
    public delegate bool ActiveResolve(FormKey fk);

    /// <summary>One internalized record: the donor-universe body plus why it was pulled in (the parent chain tail,
    /// for the closure-cap refusal message and the report).</summary>
    public sealed record ClosureItem(IMajorRecordGetter Body, string PulledBy);

    /// <summary>The appearance-closure walk result: the records to internalize (donor bodies at donor keys, the
    /// NPC itself NOT included), the links kept as-is (they resolve actively), or a loud refusal.
    /// <see cref="FetchMiss"/> marks the refusal class where the donor universe could not PRODUCE a needed record —
    /// the one class the caller's donor-read context (which files were opened, what an auto-widen did) can explain;
    /// the other refusals (template, race, cap) are about the donor's shape, and decorating them with read context
    /// would point the user at the wrong fix.</summary>
    public sealed record ClosureResult(
        bool Success, string? Error,
        IReadOnlyList<ClosureItem> ToInternalize,
        IReadOnlyList<FormKey> KeptLinks,
        bool FetchMiss = false)
    {
        public static ClosureResult Fail(string error, bool fetchMiss = false)
            => new(false, error, Array.Empty<ClosureItem>(), Array.Empty<FormKey>(), fetchMiss);
    }

    /// <summary>
    /// Walk the donor NPC's APPEARANCE seeds (HeadParts, HairColor, HeadTexture, WornArmor — the four link-bearing
    /// appearance fields; FaceMorph/FaceParts/TintLayers/TextureLighting are inline values with no links) and collect
    /// the closure of records that must be internalized under the rule in the file header. Expansion is the generic
    /// <see cref="IFormLinkContainerGetter.EnumerateFormLinks"/> walk — by construction, no per-type hand list — so a
    /// headpart's ExtraParts/TextureSet/Color, an armor's Armature, an armature's skin textures all follow the same rule.
    /// A donor-internal link whose body the donor universe cannot produce is a LOUD refusal (a half-broken donor),
    /// as is a closure past <see cref="ClosureCap"/> (a runaway — typically a donor-internal custom race).
    /// </summary>
    public static ClosureResult CollectAppearanceClosure(
        INpcGetter donor, IReadOnlySet<ModKey> donorMods, DonorFetch fetch, ActiveResolve resolvesActively)
    {
        // A donor that INHERITS its traits from a template (TemplateFlags.Traits) has EMPTY appearance fields on its
        // own record — the look lives on the template. Copying "nothing" would succeed and, in the apply lane,
        // actively WIPE the target's face (a Q3 silent wrong answer). Refuse with the real remedy.
        if (donor.Template is { IsNull: false } tpl
            && donor.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
            return ClosureResult.Fail(
                $"the donor NPC inherits its TRAITS (appearance) from a template ({tpl.FormKey}) — its own record carries " +
                "no appearance to copy, and copying the empty fields would blank the target's face. Pass the TEMPLATE " +
                "NPC as the donor instead (resolve the Template chain to the record that actually carries the look). " +
                "Nothing was written.");

        var seeds = new List<(FormKey Key, string Field)>();
        foreach (var hp in donor.HeadParts)
            if (!hp.FormKey.IsNull) seeds.Add((hp.FormKey, "HeadParts"));
        if (donor.HairColor is { IsNull: false } hc) seeds.Add((hc.FormKey, "HairColor"));
        if (donor.HeadTexture is { IsNull: false } ht) seeds.Add((ht.FormKey, "HeadTexture"));
        if (donor.WornArmor is { IsNull: false } wa) seeds.Add((wa.FormKey, "WornArmor"));

        // The RACE is a link-bearing appearance-adjacent field, but a race is NOT an internalizable subtree (it pulls
        // skeletons/body meshes/other races — the runaway the cap exists for). Donor-internal race → refuse UP FRONT
        // with the real remedy, not a cap message.
        if (donor.Race is { IsNull: false } race && (donorMods.Contains(race.FormKey.ModKey) || !resolvesActively(race.FormKey)))
            return ClosureResult.Fail(
                $"the donor NPC's Race ({race.FormKey}) is defined in the donor plugin (or does not resolve in the active " +
                "load order). Standalone-copying a custom RACE is out of this verb's scope — a race pulls skeletons, body " +
                "meshes and sibling races, not an appearance subtree. Keep the race mod installed+active as a master, or " +
                "choose a donor on a standard race. Nothing was written.");

        var toInternalize = new List<ClosureItem>();
        var kept = new List<FormKey>();
        var seen = new HashSet<FormKey>();
        var queue = new Queue<(FormKey Key, string PulledBy)>();
        foreach (var (key, field) in seeds) queue.Enqueue((key, $"Npc.{field}"));

        while (queue.Count > 0)
        {
            var (key, pulledBy) = queue.Dequeue();
            if (key.IsNull || !seen.Add(key)) continue;

            bool internalize = donorMods.Contains(key.ModKey) || !resolvesActively(key);
            if (!internalize) { kept.Add(key); continue; }

            if (toInternalize.Count >= ClosureCap)
                return ClosureResult.Fail(
                    $"the appearance closure exceeded {ClosureCap} records (last pull: {key} via {pulledBy}) — a real " +
                    "appearance subtree is small (typically well under 30 records), so this is a runaway walk (e.g. a " +
                    "donor-internal shared-resource web). Refusing rather than silently truncating (Q3). Nothing was written.");

            var body = fetch(key);
            if (body is null)
                return ClosureResult.Fail(
                    $"the donor's appearance references {key} (via {pulledBy}), which must be internalized (it is donor-" +
                    "defined or does not resolve in the active load order) — but the donor universe cannot produce that " +
                    $"record. It is defined in '{key.ModKey.FileName}': if that mod is DISABLED, enable it (or keep it " +
                    "installed as a master) and re-run. Nothing was written.", fetchMiss: true);

            toInternalize.Add(new ClosureItem(body, pulledBy));
            var label = $"{RecordNaming.StripOverlay(body.GetType().Name)} {key} ({body.EditorID ?? "<no editorid>"})";
            if (body is IFormLinkContainerGetter flc)
                foreach (var link in flc.EnumerateFormLinks())
                    if (!link.FormKey.IsNull && !seen.Contains(link.FormKey))
                        queue.Enqueue((link.FormKey, label));
        }

        return new ClosureResult(true, null, toInternalize, kept);
    }

    // ======================================================================
    //  CLONE-MODE STRIP — remove every remaining foreign link, loudly
    // ======================================================================

    /// <summary>One stripped foreign link: the field it sat on and what was removed (for the Q3 report — every
    /// strip is named, so "standalone" never silently means "quietly different").</summary>
    public sealed record StripReport(string Field, string Removed);

    /// <summary>The strip-pass result: what was stripped (each named), or a loud refusal (a REQUIRED foreign link
    /// this pass cannot remove without inventing data — e.g. a donor-internal Class).</summary>
    public sealed record StripResult(bool Success, string? Error, IReadOnlyList<StripReport> Stripped)
    {
        public static StripResult Fail(string error) => new(false, error, Array.Empty<StripReport>());
    }

    /// <summary>
    /// Remove every link on <paramref name="record"/> for which <paramref name="isForeign"/> holds (after the
    /// appearance remap, those are the donor's NON-appearance references — factions, outfits, packages, VMAD, …).
    /// Reflection walk over the record's own properties, one strip rule per shape:
    ///   • nullable FormLink → set null;   • list of FormLinks → remove the foreign entries;
    ///   • list of link-BEARING elements (Factions' RankPlacement, Items' ContainerEntry, …) → remove the elements
    ///     carrying a foreign link;   • nullable link-bearing substruct (VMAD, Sound…) → set the property null;
    ///   • a REQUIRED (non-nullable, non-list) foreign link → LOUD refusal, never a silent keep (Q3 — keeping it
    ///     would master the donor and the "standalone" claim would be silently false).
    /// Every strip is reported by field name.
    /// </summary>
    public static StripResult StripForeignLinks(IMajorRecord record, Func<FormKey, bool> isForeign)
    {
        var stripped = new List<StripReport>();
        foreach (var prop in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            if (prop.Name is "FormKey" or "EditorID") continue;
            object? val;
            try { val = prop.GetValue(record); } catch { continue; }
            if (val is null) continue;

            // 1. A single FormLink property (nullable or required).
            if (val is IFormLinkGetter singleLink)
            {
                if (singleLink.FormKeyNullable is not { } fk || fk.IsNull || !isForeign(fk)) continue;
                if (TrySetLinkNull(val))
                    stripped.Add(new StripReport(prop.Name, fk.ToString()));
                else
                    return StripResult.Fail(
                        $"the clone's REQUIRED field '{prop.Name}' points at {fk}, which is donor-internal (or unresolvable) " +
                        "and cannot be nulled or removed — stripping it would invent data, keeping it would silently master " +
                        "the donor. Use the apply lane instead: scaffold your own NPC (with your own " +
                        $"{prop.Name}) and copy the donor's appearance onto it. Nothing was written.");
                continue;
            }

            // 2. A list — of FormLinks directly, or of link-bearing elements.
            if (val is IList list && val is not string)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var el = list[i];
                    if (el is IFormLinkGetter elLink)
                    {
                        if (elLink.FormKeyNullable is { } lk && !lk.IsNull && isForeign(lk))
                        { list.RemoveAt(i); stripped.Add(new StripReport($"{prop.Name}[{i}]", lk.ToString())); }
                    }
                    else if (el is IFormLinkContainerGetter elc)
                    {
                        var foreignKeys = elc.EnumerateFormLinks()
                            .Where(l => !l.FormKey.IsNull && isForeign(l.FormKey))
                            .Select(l => l.FormKey.ToString()).Distinct().ToList();
                        if (foreignKeys.Count > 0)
                        { list.RemoveAt(i); stripped.Add(new StripReport($"{prop.Name}[{i}]", string.Join(", ", foreignKeys))); }
                    }
                }
                continue;
            }

            // 3. A link-bearing substruct / polymorphic field (VMAD, Sound, …): if ANY of its links is foreign,
            //    null the whole property (they are optional adornments on an NPC) — or refuse loud if it can't be nulled.
            if (val is IFormLinkContainerGetter sub)
            {
                var foreign = sub.EnumerateFormLinks()
                    .Where(l => !l.FormKey.IsNull && isForeign(l.FormKey))
                    .Select(l => l.FormKey.ToString()).Distinct().ToList();
                if (foreign.Count == 0) continue;
                if (prop.CanWrite)
                { prop.SetValue(record, null); stripped.Add(new StripReport(prop.Name, string.Join(", ", foreign))); }
                else
                    return StripResult.Fail(
                        $"the clone's field '{prop.Name}' carries donor-internal (or unresolvable) reference(s) " +
                        $"({string.Join(", ", foreign)}) and the property cannot be cleared. Use the apply lane " +
                        "(scaffold your own NPC, copy the appearance onto it). Nothing was written.");
            }
        }
        return new StripResult(true, null, stripped);
    }

    /// <summary>Null a single FormLink property's key iff the link is genuinely NULLABLE — the record model's
    /// <c>IFormLinkNullableGetter</c>, not the presence of a SetToNull method: in Mutagen 0.53.1 the REQUIRED
    /// <c>FormLink&lt;T&gt;</c> ALSO exposes SetToNull (review finding — method-presence made the required-link
    /// refusal dead code and silently wrote a NULL required field, e.g. a clone with Class=00000000). Returns
    /// false for a required link, which the caller escalates to the loud refusal.</summary>
    static bool TrySetLinkNull(object link)
    {
        bool nullable = link.GetType().GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkNullable<>));
        if (!nullable) return false;                                      // required by the record model — never nulled
        var m = link.GetType().GetMethod("SetToNull", Type.EmptyTypes);
        if (m is null) return false;
        m.Invoke(link, null);
        return true;
    }

    // ======================================================================
    //  BUILD + WRITE — the patch-mod construction half (core, WritePatchBuilder-style:
    //  the mcp service does lanes/folders/MO2; this does records + serialize)
    // ======================================================================

    /// <summary>
    /// Build the copy into a patch mod at <paramref name="outPath"/> and serialize it: allocate new 0x800+ keys,
    /// Duplicate + RemapLinks the closure (+ the clone), run the mode lane (clone strip / apply field-copy), write
    /// multi-master, read back the header. <paramref name="mastersFor"/> supplies the known-master set for the
    /// serialize, keyed by the patch filename (the caller's session releases any overlay on the target first — the
    /// active-patch self-lock fix). Returns the outcome WITHOUT the asset half (the service appends it — assets need
    /// the MO2 asset view, which is the service's). Every refusal is loud with nothing usable written (Q3).
    /// </summary>
    public static NpcCopyOutcome BuildAndWrite(
        INpcGetter donorNpc, IReadOnlySet<ModKey> donorMods, ClosureResult closure,
        bool clone, string? newEditorid, string? newName,
        FormKey targetFk, INpcGetter? targetActiveBody,
        ActiveResolve resolvesActively,
        string outPath, bool extend,
        Func<string, IReadOnlyList<ISkyrimModGetter>> mastersFor,
        string donorReadFrom, bool donorOutOfLoadOrder,
        Func<Exception, string>? serializeFailure = null)
    {
        var patchFileName = Path.GetFileName(outPath);
        try
        {
            SkyrimMod patchMod;
            if (extend)
            {
                try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
                catch (Exception ex) { return NpcCopyOutcome.Fail($"could not open '{patchFileName}' to extend: {ex.Message}"); }
            }
            else
                patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.EnsureFormIdFloor(patchMod);

            // ---- extend-lane DEDUPE (review finding): a PRIOR run may already have internalized this donor's
            //      records into the patch. Re-copying would put TWO records with the same preserved EditorID in one
            //      plugin — ambiguous for the engine's facegeom block-name mapping, the exact invariant EditorID
            //      preservation protects. Match by type + EditorID (both preserved by design) → REUSE the existing
            //      copy: map the donor key to it and skip the duplicate. Each reuse is reported.
            var toCopy = new List<IMajorRecordGetter>();
            var dict = new Dictionary<FormKey, FormKey>();
            var reused = new List<string>();
            if (extend)
            {
                // Group patch-originating records by type + EditorID — OrdinalIgnoreCase (matching the clone
                // dup-check and the create-path upsert; review finding: an ordinal compare re-copied a case-differing
                // prior copy, minting the exact duplicate this dedupe prevents) and DUPLICATE-TOLERANT (same-named
                // records exist in the wild per the nested-create re-run semantics; a raw ToDictionary threw).
                var groups = new Dictionary<string, List<IMajorRecordGetter>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in patchMod.EnumerateMajorRecords())
                {
                    if (r.FormKey.ModKey != patchMod.ModKey || string.IsNullOrEmpty(r.EditorID)) continue;
                    var key = RecordNaming.StripOverlay(r.GetType().Name) + "\0" + r.EditorID;
                    if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<IMajorRecordGetter>();
                    l.Add(r);
                }
                // Pass 1 — pair up name matches; the candidate map must be COMPLETE before any content check,
                // because matched records reference each other (a headpart → its texture set).
                var matches = new List<(ClosureItem Item, IMajorRecordGetter Existing)>();
                var candDict = new Dictionary<FormKey, FormKey>();
                foreach (var i in closure.ToInternalize)
                {
                    var type = RecordNaming.StripOverlay(i.Body.GetType().Name);
                    var edid = i.Body.EditorID;
                    if (string.IsNullOrEmpty(edid) || !groups.TryGetValue(type + "\0" + edid, out var cands))
                    { toCopy.Add(i.Body); continue; }
                    if (cands.Count > 1)
                        return NpcCopyOutcome.Fail(
                            $"'{patchFileName}' already contains {cands.Count} {type} records with EditorID '{edid}' — matching this " +
                            "donor's record against them is ambiguous, and re-copying would add a third. Clean the duplicates up (or " +
                            "copy into a different patch). Nothing was written.");
                    matches.Add((i, cands[0]));
                    candDict[i.Body.FormKey] = cands[0].FormKey;
                }
                // Pass 2 — CONTENT equality (review finding): a prior copy is reused only when it IS this donor's
                // record. Same-named but DIFFERENT records are a real load-order fact (KS Hairdos-derived NPCs reuse
                // headpart EditorIDs across mods) — reusing one would silently wire this NPC to the OTHER donor's
                // part; re-copying would duplicate the EditorID and break facegeom block-name identity. So: refuse
                // loud, naming the collision. Equality is judged on a probe duplicate under the existing key with
                // sibling links mapped to their candidates — i.e. "would copying produce exactly this record?"
                foreach (var (item, existing) in matches)
                {
                    var probe = item.Body.Duplicate(existing.FormKey);
                    probe.RemapLinks(candDict);
                    if (!probe.Equals(existing))
                        return NpcCopyOutcome.Fail(
                            $"'{patchFileName}' already contains {RecordNaming.StripOverlay(existing.GetType().Name)} '{existing.EditorID}' " +
                            $"({existing.FormKey}) from a previous copy, and its CONTENT differs from this donor's {item.Body.FormKey} — " +
                            "reusing it would wire this NPC to the other donor's record; re-copying would duplicate the EditorID and " +
                            "break the facegeom block-name mapping. Copy into a DIFFERENT patch. Nothing was written.");
                    dict[item.Body.FormKey] = existing.FormKey;
                    reused.Add($"{RecordNaming.StripOverlay(existing.GetType().Name)} '{existing.EditorID}'  {item.Body.FormKey} → {existing.FormKey} (already in the patch, content-identical — reused, not re-copied)");
                }
            }
            else toCopy.AddRange(closure.ToInternalize.Select(i => i.Body));
            if (clone)
            {
                // a clone re-run into the same patch would mint a SECOND NPC with the same EditorID — refuse with the
                // real choice instead (the dedupe above deliberately covers only the appearance subtree).
                if (extend && patchMod.Npcs.Any(n => string.Equals(n.EditorID, newEditorid!.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return NpcCopyOutcome.Fail(
                        $"'{patchFileName}' already contains an NPC with EditorID '{newEditorid!.Trim()}' — cloning again " +
                        "would duplicate it. Pick a different new_editorid, or target the existing NPC via target_formid=.");
                toCopy.Add(donorNpc);
            }

            // ---- allocate NEW keys (the patch's own 0x800+ counter) for what actually gets copied ----
            uint next = patchMod.ModHeader.Stats.NextFormID;
            foreach (var rec in toCopy)
            {
                if (FormIdRange.ObjectIdSpaceExhausted(next))
                    return NpcCopyOutcome.Fail($"cannot allocate a new FormID: the patch's NextObjectID counter is past 0x{FormIdRange.ObjectIdMax:X}.");
                dict[rec.FormKey] = new FormKey(patchMod.ModKey, next++);
            }
            patchMod.ModHeader.Stats.NextFormID = next;

            // ---- duplicate + remap in a SCRATCH mod (review finding): RenumberRecordsInto's whole-mod RemapLinks
            //      honors its fresh-target contract there, so an EXTENDED patch's PRE-EXISTING records are never
            //      silently repointed (a user record deliberately referencing the active donor stays untouched).
            //      The scratch shares the patch's ModKey, so the allocated keys are final; the remapped copies are
            //      then transplanted into the real patch. EditorIDs preserved by Duplicate (block-name identity).
            var scratch = new SkyrimMod(patchMod.ModKey, SkyrimRelease.SkyrimSE);
            var ren = RemapEngine.RenumberRecordsInto(scratch, toCopy, dict);
            if (!ren.Success) return NpcCopyOutcome.Fail(ren.Error!);
            foreach (var rec in scratch.EnumerateMajorRecords())
                if (!RemapEngine.TryAddToFlatGroup(patchMod, (IMajorRecord)rec))
                    return NpcCopyOutcome.Fail(
                        $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} could not be transplanted into the patch (engine inconsistency, Q3). Nothing usable was written.");

            // ---- the internalized report — built BEFORE serialize (review finding: donor bodies can be backed by a
            //      session overlay the master set releases at serialize; reading them afterwards is a disposed-mmap read).
            var internalized = closure.ToInternalize
                .Where(i => dict.ContainsKey(i.Body.FormKey) && toCopy.Any(t => t.FormKey == i.Body.FormKey))
                .Select(i => new InternalizedRecord(
                    RecordNaming.StripOverlay(i.Body.GetType().Name), i.Body.EditorID ?? "<no editorid>",
                    i.Body.FormKey, dict[i.Body.FormKey], i.PulledBy))
                .ToList();

            FormKey newNpcKey;
            var copiedFields = (IReadOnlyList<string>)Array.Empty<string>();
            var strippedReports = (IReadOnlyList<StripReport>)Array.Empty<StripReport>();
            Func<FormKey, bool> isForeign = fk2 =>
                donorMods.Contains(fk2.ModKey) || (fk2.ModKey != patchMod.ModKey && !resolvesActively(fk2));

            if (clone)
            {
                newNpcKey = dict[donorNpc.FormKey];
                var cloneNpc = patchMod.Npcs.FirstOrDefault(n => n.FormKey == newNpcKey)
                    ?? throw new InvalidOperationException("the clone vanished from the patch after renumber (engine inconsistency, Q3).");
                cloneNpc.EditorID = newEditorid!.Trim();
                if (!string.IsNullOrWhiteSpace(newName)) cloneNpc.Name = newName.Trim();

                // strip every remaining donor-internal / unresolvable link — each named, or a loud refusal.
                var strip = StripForeignLinks(cloneNpc, isForeign);
                if (!strip.Success) return NpcCopyOutcome.Fail(strip.Error!);
                strippedReports = strip.Stripped;
            }
            else
            {
                // apply lane: the target NPC — an active winner the service fetched, or (not-yet-enabled patch) the
                // extend target's own record.
                Npc targetOverride;
                if (targetFk.ModKey == patchMod.ModKey)
                {
                    targetOverride = patchMod.Npcs.FirstOrDefault(n => n.FormKey == targetFk)
                        ?? throw new InvalidOperationException(
                            $"{targetFk} names this patch, but '{patchFileName}' defines no such NPC. Create the NPC first ({ToolNames.Create}) or pass an active NPC's formid.");
                }
                else if (targetActiveBody is not null)
                {
                    targetOverride = (Npc)WriteEngine.GenericGetOrAddAsOverride(patchMod, targetActiveBody);
                }
                else
                    return NpcCopyOutcome.Fail("apply lane reached the build without a target body (engine inconsistency, Q3).");

                newNpcKey = targetFk;
                copiedFields = CopyAppearanceFields(donorNpc, targetOverride);
                targetOverride.RemapLinks(dict);   // repoint ONLY the override's just-copied fields (never the rest of an extended patch)

                // belt-and-braces (Q3): nothing DONOR-INTERNAL may remain on the override after the remap. Scoped to
                // donor keys only (review finding): a target's PRE-EXISTING dangling link (mod-update dirt whose master
                // is still declared) is not this operation's defect and must not hard-block a legitimate copy.
                var leak = ((IFormLinkContainerGetter)targetOverride).EnumerateFormLinks()
                    .FirstOrDefault(l => !l.FormKey.IsNull && donorMods.Contains(l.FormKey.ModKey));
                if (leak is not null && !leak.FormKey.IsNull)
                    return NpcCopyOutcome.Fail(
                        $"after the copy the target still references {leak.FormKey} in the donor's plugin — refusing to " +
                        "write a patch that would master the donor (Q3). If the target deliberately references the donor " +
                        "in a non-appearance field (an outfit, a faction), remove that first or clone instead. Nothing was written.");
            }

            // ---- HARVEST asset paths from the IN-PATCH duplicates, pre-serialize (they are plain in-memory records;
            //      the donor-overlay bodies may be released before the service's asset carry runs — review finding).
            var newKeys = internalized.Select(i => i.NewKey).ToHashSet();
            var duplicates = patchMod.EnumerateMajorRecords().Where(r => newKeys.Contains(r.FormKey)).Cast<IMajorRecordGetter>().ToList();
            var harvested = NpcAppearanceAssets.HarvestAssetPaths(duplicates);

            // ---- serialize (multi-master; the caller's mastersFor handles the active-patch self-lock) ----
            try { WriteEngine.WritePatch(patchMod, mastersFor(patchFileName), outPath); }
            // #314 — this lane serializes through the caller's AllMastersExcept too, so it is subject to the same
            // unopenable-master skip and the same residual refusal; without the caller's cause it reported an unnamed
            // engine fault while the identical failure from apply/create/forward named the plugin (PR #315 review).
            // Injected rather than reached for: this type takes a mastersFor closure, not a resolver session.
            //
            // An OVERRIDE, not a suffix (PR #315 review 4): a suffix left the baseline refusal rendered behind
            // "serialize failed" (it threw while the master-set ARGUMENT was built, so serialize never started) with
            // the write status stated twice and a doubled period — the exact three complaints review 3 fixed
            // everywhere else, surviving here because this was the one lane routed around SerializeFailure.
            catch (Exception ex)
            {
                return NpcCopyOutcome.Fail(serializeFailure?.Invoke(ex)
                    ?? $"serialize failed — {WriteEngine.Describe(ex)}. Nothing usable was written.");
            }

            // ---- post-commit read-back — the patch IS on disk from here; a read-back failure is a WARNING on a
            //      success, never a "nothing was written" (review finding: that mislabel invites a duplicate re-run).
            var masters = new List<string>();
            long bytes = 0;
            bool donorAmongMasters = false;
            string? warning = null;
            try
            {
                var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                try
                {
                    masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
                    bytes = new FileInfo(outPath).Length;
                }
                finally { (back as IDisposable)?.Dispose(); }
                donorAmongMasters = masters.Any(m => { try { return donorMods.Contains(ModKey.FromFileName(m)); } catch { return false; } });
            }
            catch (Exception ex)
            {
                warning = $"the patch WAS written, but the post-write read-back failed ({ex.Message}) — the masters list " +
                          "could not be verified this call. Do NOT re-run blindly (that would mint a duplicate patch); " +
                          "inspect the plugin with " + ToolNames.ReadPluginFile + ".";
            }

            return new NpcCopyOutcome(
                true, null, clone ? "clone" : "apply",
                donorNpc.FormKey, donorReadFrom, donorOutOfLoadOrder,
                newNpcKey, outPath, extend,
                internalized, reused, closure.KeptLinks.Count,
                copiedFields, strippedReports, masters, donorAmongMasters, donorMods.Count == 0,
                harvested, null, bytes, warning);
        }
        catch (Exception ex)
        {
            return NpcCopyOutcome.Fail($"copy failed — {WriteEngine.Describe(ex)}. Nothing usable was written.");
        }
    }

    // ======================================================================
    //  APPLY-MODE FIELD COPY — donor appearance onto an EXISTING target NPC
    // ======================================================================

    /// <summary>
    /// Copy the donor's appearance FIELDS onto <paramref name="target"/> (an override the caller already placed in
    /// the patch). Whole-field copies via Mutagen DeepCopy/SetTo — the same no-field-left-behind property the clone
    /// lane gets from Duplicate: TintLayers, TextureLighting (the dark-skin default trap), FaceMorph/FaceParts,
    /// Weight/Height all carry the donor's exact values. Links are copied at their DONOR keys; the caller's
    /// RemapLinks pass repoints the internalized ones. Returns the copied-field names for the report — including a
    /// prominent Race note when the donor's race differs (facegen is race-fitted; not copying it would neck-seam).
    /// </summary>
    public static IReadOnlyList<string> CopyAppearanceFields(INpcGetter donor, Npc target)
    {
        var copied = new List<string>();

        target.HeadParts.SetTo(donor.HeadParts.Select(l => new FormLink<IHeadPartGetter>(l.FormKey)));
        copied.Add($"HeadParts ({donor.HeadParts.Count})");

        target.HairColor.SetTo(donor.HairColor?.FormKeyNullable);
        copied.Add("HairColor");
        target.HeadTexture.SetTo(donor.HeadTexture?.FormKeyNullable);
        copied.Add("HeadTexture");
        target.WornArmor.SetTo(donor.WornArmor?.FormKeyNullable);
        copied.Add("WornArmor");

        target.FaceMorph = donor.FaceMorph?.DeepCopy();
        copied.Add("FaceMorph");
        target.FaceParts = donor.FaceParts?.DeepCopy();
        copied.Add("FaceParts");

        target.TintLayers.SetTo(donor.TintLayers.Select(t => t.DeepCopy()));
        copied.Add($"TintLayers ({donor.TintLayers.Count})");

        target.TextureLighting = donor.TextureLighting;
        copied.Add("TextureLighting");

        target.Weight = donor.Weight;
        target.Height = donor.Height;
        copied.Add("Weight/Height");

        if (target.Race.FormKey != donor.Race.FormKey)
        {
            target.Race.SetTo(donor.Race.FormKey);
            copied.Add($"Race (target's differed — copied {donor.Race.FormKey}; facegen is race-fitted)");
        }

        // GENDER (review finding): headparts, tint masks and the baked facegen are all gender-fitted — a female
        // donor's look on a male-flagged target hands the engine female headparts under a male body/skeleton (the
        // wrong-head/dark-face class this verb exists to kill). Match the Female flag to the donor, loudly.
        bool donorFemale = donor.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        bool targetFemale = target.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        if (donorFemale != targetFemale)
        {
            if (donorFemale) target.Configuration.Flags |= NpcConfiguration.Flag.Female;
            else target.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
            copied.Add($"Configuration.Flags.Female (target's gender differed — matched to the donor; headparts/facegen are gender-fitted)");
        }

        return copied;
    }
}

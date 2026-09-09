using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The FACEGEN sweep: for every NPC in scope, which mod wins the head <c>.nif</c>, which wins the face
/// <c>.dds</c>, and which plugin wins the <c>NPC_</c> record behind them — joined into ONE row per NPC.
///
/// <para>A dark/grey face is the DESYNC between two independent precedences: the MO2 VFS decides the two files,
/// plugin load order decides the record. xEdit sees only the second, which is why a desynced face shows no
/// conflict there. Both halves live in this process already, so the join is server-side.</para>
///
/// <para><b>Nothing here is newly modelled.</b> The path is <see cref="FaceGenPath.For"/> (a pure transform of the
/// FormKey — folder = the DEFINING master, file = the index byte masked to <c>00</c> plus the 6-hex local id); the
/// file winners are <see cref="AssetResolver"/>; the record winner and the face-field comparison are
/// <see cref="ReadEngine"/> + <see cref="FieldsDiff"/>.</para>
///
/// <para><b>The population is the UNION</b>: every <c>NPC_</c> in scope that needs a bake, PLUS every facegen file
/// on disk whose key resolves to nothing. Enumerating from the files alone misses an NPC that has no facegen at
/// all; enumerating from the records alone misses an orphaned folder. Neither half is the answer on its own.</para>
///
/// <para><b>What is excluded, not flagged</b>: an NPC whose <c>Template</c> is set with the <c>Traits</c> flag
/// inherits its appearance and has no bake of its own, so the keyed path is never read for it. It is counted as
/// excluded and never reported as a fault.</para>
/// </summary>
public static class FaceGenCheck
{
    /// <summary>The face fields the bake is built from — the comparison surface for <see cref="FaceGenFindingClass.StaleBake"/>.
    /// <c>HairColor</c> is read and compared with the rest, but a disagreement on it ALONE does not touch the bake
    /// and is not a flag (see <see cref="HairColorPath"/>).</summary>
    public static readonly IReadOnlyList<string> FaceFields = new[]
    {
        "HeadParts", "FaceMorph", "FaceParts", "TintLayers", "HairColor", "HeadTexture", "TextureLighting",
    };

    /// <summary>The one field whose disagreement is not a stale bake: hair colour is applied at runtime and the
    /// baked head geometry and tint do not carry it.</summary>
    public const string HairColorPath = "HairColor";

    /// <summary>The marker <see cref="FieldsDiff"/> puts on a list whose items are the SAME but in a different
    /// order. Not a stale bake: the Creation Kit bakes from the values, not from the order a plugin wrote them in,
    /// and on the measured order this alone accounted for most of the class.</summary>
    public const string OrderOnlyDelta = "ORDER DIFFERS from";

    /// <summary>The two Data-relative folder trees a facegen bake lives under, for the file half of the union.</summary>
    public const string GeomRoot = @"meshes\actors\character\facegendata\facegeom";
    public const string TintRoot = @"textures\actors\character\facegendata\facetint";

    static readonly Type[] NpcTypes = { typeof(INpcGetter) };

    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null/empty = every NPC in the active order).
    /// <paramref name="assets"/> is a captured asset build, so every path in one sweep answers off one VFS build.
    /// <paramref name="pluginsInMod"/> maps a provider (an MO2 mod folder, "overwrite" or "Data") to the plugin
    /// filenames it ships — the pole a <see cref="FaceGenFindingClass.StaleBake"/> comparison runs against. It is a
    /// fact about the MO2 composition, which lives above core, so it is handed in rather than re-derived here.
    /// <para><paramref name="limit"/> caps how many findings are COLLECTED; the totals are always counted whole.
    /// <paramref name="countsOnly"/> collects no rows and returns the by-class and by-owning-mod histograms.</para></summary>
    public static FaceGenCheckResult Run(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                                         AssetResolver.AssetView assets,
                                         Func<string, IReadOnlyList<string>> pluginsInMod,
                                         IReadOnlyList<string>? scope, int limit,
                                         IReadOnlyList<(string Name, string Path)>? offOrder = null,
                                         SweepScope? recordScope = null,
                                         FaceGenFindingClass classes = FaceGenFindingClass.All,
                                         bool countsOnly = false,
                                         SweepExclusion.Resolved? exclude = null)
    {
        var findings = new List<FaceGenFinding>();
        var withheld = new List<FaceGenFinding>();
        var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var byMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalFound = 0, npcsScanned = 0, templated = 0, noPole = 0, noFaceGenRace = 0, raceUnresolved = 0;
        int excludedFromScope = 0;
        var raceMemo = new Dictionary<FormKey, bool?>();
        bool listFamilySplit = classes != FaceGenFindingClass.All && classes.HasFlag(FaceGenFindingClass.FamilySplit);

        // The scope's plugin targets. An off-order file is swept as its own overlay, exactly as the errors family
        // does it: its records with the file's own definitions in play.
        var targets = new List<string>();
        if (scope is { Count: > 0 })
        {
            foreach (var p in scope)
            {
                var name = (p ?? "").Trim();
                if (name.Length == 0) continue;
                if (!view.ContainsPlugin(name)) continue;              // the caller split off-order already
                if (!targets.Contains(name, StringComparer.OrdinalIgnoreCase)) targets.Add(name);
            }
        }
        // The exclusion axis, applied to the SWEEP on EVERY lane — a plugins= scope and the whole order alike. An
        // excluded plugin contributes no NPCs either way, and a name the caller TYPED that is in no lane's scope is
        // a typo and refuses, exactly as the errors family refuses it.
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exclude is { } ex)
        {
            var inScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scope is { Count: > 0 })
            {
                foreach (var t in targets) inScope.Add(t);
                if (offOrder is { Count: > 0 }) foreach (var o in offOrder) inScope.Add(o.Name);
            }
            else
                foreach (var n in resolver.PluginNames)
                    if (!view.ExcludedPlugins.ContainsKey(n)) inScope.Add(n);

            // Only the names the CALLER TYPED are held against the scope; a group member that is not here is the
            // ordinary case.
            foreach (var name in ex.TypedNames)
                if (!inScope.Contains(name))
                    return FaceGenCheckResult.Fail(
                        $"exclude= names '{name}', which is not in the scope this facegen sweep would cover.{view.AbsenceClause(name)} "
                        + "Nothing was swept — an exclusion that matches nothing would return the findings you asked to leave out.")
                           with { Epoch = view.Epoch };

            foreach (var n in ex.Names) dropped.Add(n);
            int scopeBefore = inScope.Count;
            targets.RemoveAll(dropped.Contains);
            if (offOrder is { Count: > 0 }) offOrder = offOrder.Where(o => !dropped.Contains(o.Name)).ToList();
            inScope.RemoveWhere(dropped.Contains);
            excludedFromScope = scopeBefore - inScope.Count;
            if (inScope.Count == 0)
                return FaceGenCheckResult.Fail(
                    $"exclude= removed every plugin this facegen sweep would have covered ({scopeBefore} in scope, all excluded) — "
                    + "there is nothing left to check. Narrow exclude=, or widen plugins=.") with { Epoch = view.Epoch };
        }

        // The NPCs this sweep judges, and the record body it judges each from. Keyed so the file half below can ask
        // whether a file on disk belongs to an NPC at all in one lookup.
        var judged = new HashSet<FormKey>();
        var offOrderScanned = new List<string>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? scanError = null;

        using var session = resolver.OpenSession();

        void Judge(FormKey fk, IMajorRecordGetter body, string winnerPlugin, bool offOrderFile)
        {
            if (recordScope is not null && !recordScope.Matches(fk, body)) return;
            if (!judged.Add(fk)) return;
            npcsScanned++;
            if (body is not INpcGetter npc) return;
            if (InheritsAppearance(npc)) { templated++; return; }
            // A race without the FaceGenHead flag has no baked head at all — a horse, a dragon, a draugr shell.
            // The flag is Mutagen's own (Race.Flag.FaceGenHead), so this is not a hand-kept race list, and without
            // it a whole-order sweep reports every creature actor as missing a bake it never had.
            switch (RaceBakes(npc.Race.FormKey))
            {
                case false: noFaceGenRace++; return;
                case null: raceUnresolved++; return;
            }
            Classify(fk, body, winnerPlugin, offOrderFile);
        }

        // Is this NPC excluded from the whole-order lane? exclude= narrows the SELECTION, not the judgement — the
        // same thing it does to a plugins= scope — so an NPC is out only when EVERY plugin touching it is excluded.
        // The winner is tested first, which is free: an NPC whose winner is kept is in scope whatever else touches it.
        bool ExcludedOut(FormKey fk, string winner)
        {
            if (dropped.Count == 0 || !dropped.Contains(winner)) return false;
            var touching = view.TouchingPlugins(fk);
            return touching is null || touching.All(dropped.Contains);
        }

        // Does this race bake a head? Memoized per race, so a 66,000-NPC order pays one record read per race.
        // null = the race could not be read, which is NOT the same as "does not bake": the NPC is left out and
        // counted, so a race nobody could resolve never reads as a clean or a broken bake.
        bool? RaceBakes(FormKey raceKey)
        {
            if (raceMemo.TryGetValue(raceKey, out var had)) return had;
            bool? answer = null;
            if (view.ResolveWinner(raceKey) is { } rw
                && view.GetRecord(session, rw.WinnerPlugin, raceKey, typeof(IRaceGetter)) is IRaceGetter race)
                answer = race.Flags.HasFlag(Race.Flag.FaceGenHead);
            return raceMemo[raceKey] = answer;
        }

        try
        {
            if (targets.Count > 0)
            {
                // A plugin scope selects the NPCs those plugins touch, then judges each at its LOAD-ORDER WINNER —
                // "audit what this mod's NPCs will render as" is a question about the game's answer, not about the
                // named plugin's own copy.
                foreach (var (fk, _, _, _) in view.RecordsIn(targets, NpcTypes))
                {
                    if (judged.Contains(fk)) continue;
                    if (view.ResolveWinner(fk) is not { } w) continue;
                    if (view.GetRecord(session, w.WinnerPlugin, fk, typeof(INpcGetter)) is not { } body) continue;
                    Judge(fk, body, w.WinnerPlugin, offOrderFile: false);
                }
            }
            else if (scope is not { Count: > 0 })
            {
                foreach (var (fk, _, body) in view.WinnerRecordsOfType(NpcTypes))
                {
                    var winner = view.ResolveWinner(fk)?.WinnerPlugin ?? fk.ModKey.FileName.String;
                    if (ExcludedOut(fk, winner)) continue;
                    Judge(fk, body, winner, offOrderFile: false);
                }
            }

            if (offOrder is { Count: > 0 })
                foreach (var (name, path) in offOrder)
                {
                    offOrderScanned.Add(name);
                    ISkyrimModGetter? ov = null;
                    try
                    {
                        ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                        foreach (var body in SweepScope.RecordsFrom(ov, new SweepScope(null, null, NpcTypes, "NPC_")))
                            Judge(body.FormKey, body, name, offOrderFile: true);
                    }
                    catch (Exception e) { scanError ??= $"{name}: {e.GetType().Name}: {e.Message}"; }
                    finally { (ov as IDisposable)?.Dispose(); }
                }
        }
        catch (Exception e)
        {
            return FaceGenCheckResult.Fail(
                $"the facegen sweep could not finish — {e.GetType().Name}: {e.Message}. Narrow it with plugins= and "
                + "retry; a partial sweep is not returned, because a short list would read as a clean order.")
                   with { Epoch = view.Epoch };
        }

        // ---- the FILE half of the union -------------------------------------------------------------
        // Every facegen file on disk whose key belongs to no NPC this sweep judged. Enumerated ONCE over the two
        // trees; a file under an in-scope NPC's key was already answered above and is skipped here.
        int filesSeen = 0;
        // Only meaningful over the WHOLE order: under ANY narrowing — plugins=, exclude=, or a record scope like
        // formids= / editorid_contains= — a file for an NPC outside the narrowing is not inert, it is simply out of
        // scope, and reporting it would be a claim about records this call never read. It would also cost one flat
        // enumeration of the winning plugin per unjudged file, which on a whole order is every facegen file on disk.
        bool wholeOrder = scope is not { Count: > 0 } && (offOrder is null || offOrder.Count == 0)
                       && recordScope is null && dropped.Count == 0;
        if (wholeOrder)
        {
            // One canonical set per TREE, not one across both: the mesh and the tint are separate files under the
            // same master folder name, so a 00-prefixed .dds must not vouch for a foreign-index .nif beside it.
            var seenGeom = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
            var seenTint = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<uint>> SeenIn(bool mesh) => mesh ? seenGeom : seenTint;
            var files = new List<(string Folder, string Name, bool Mesh)>();
            foreach (var (root, mesh) in new[] { (GeomRoot, true), (TintRoot, false) })
                foreach (var rel in assets.EnumerateUnder(root))
                {
                    var tail = rel.Length > root.Length + 1 ? rel[(root.Length + 1)..] : "";
                    int cut = tail.IndexOf('\\');
                    if (cut <= 0) continue;                               // a loose file directly under the root: no master folder
                    files.Add((tail[..cut], tail[(cut + 1)..], mesh));
                }
            filesSeen = files.Count;

            // Pass 1: which 6-hex local ids each folder has a CANONICAL (00-prefixed) file for. A non-canonical
            // file is a foreign index only against one of these, so both passes are needed.
            foreach (var (folder, name, mesh) in files)
                if (ParseKeyFile(name) is { } p && p.Index == 0)
                {
                    var seen = SeenIn(mesh);
                    if (!seen.TryGetValue(folder, out var set)) seen[folder] = set = new HashSet<uint>();
                    set.Add(p.Local);
                }

            foreach (var (folder, name, mesh) in files)
            {
                var parsed = ParseKeyFile(name);
                if (parsed is null)
                {
                    Emit(FaceGenFindingClass.Inert, null, folder, null, null, null,
                         $"{folder}\\{name} — the filename is not the eight-hex form a bake is keyed by");
                    continue;
                }
                var (index, local) = parsed.Value;
                if (!view.ContainsPlugin(folder))
                {
                    Emit(FaceGenFindingClass.Inert, null, folder, null, null, null,
                         $"{folder}\\{name} — '{folder}' is not a plugin in this load order, so the engine never reads this path");
                    continue;
                }
                var fk = new FormKey(ModKey.FromFileName(folder), local);
                if (index != 0)
                {
                    // A same-local-id file carrying somebody else's load-order index byte. INFERRED from the file
                    // itself, not from the CK: the canonical path is the 00-prefixed one, and this file sits beside
                    // it (or in place of it) under a name the engine does not look up.
                    if (!SeenIn(mesh).TryGetValue(folder, out var set) || !set.Contains(local))
                        Emit(FaceGenFindingClass.ForeignIndex, fk, folder, null, null, null,
                             $"{folder}\\{name} — index byte {index:X2}, not the canonical 00; no 00{local:X6}"
                             + $"{Path.GetExtension(name)} exists beside it");
                    continue;
                }
                if (judged.Contains(fk)) continue;                         // answered by the record half above
                if (view.ResolveWinner(fk) is not { } w)
                {
                    Emit(FaceGenFindingClass.Inert, fk, folder, null, null, null,
                         $@"{folder}\{name} — no plugin in this order defines this FormID, so no NPC reads this bake");
                    continue;
                }
                // Resolves to something that is not an NPC at all — the PlacedNpc case both gate arms got wrong.
                // A null body is the plugin having changed on disk since the index was built: reported as its own
                // row rather than thrown, so one moved plugin cannot kill a whole-order sweep.
                var body = view.GetRecord(session, w.WinnerPlugin, fk);
                if (body is null)
                    Emit(FaceGenFindingClass.Inert, fk, folder, w.WinnerPlugin, null, null,
                         $@"{folder}\{name} — {w.WinnerPlugin} no longer holds this FormID; it changed on disk after this order was indexed. Re-run the check.");
                else if (body is not INpcGetter)
                    Emit(FaceGenFindingClass.Inert, fk, folder, w.WinnerPlugin, null, null,
                         $@"{folder}\{name} — this FormID is a {TypeNameOf(body)}, not an NPC, so no actor reads this bake");
            }
        }

        var histClass = SweepFindings.Histogram(byClass);
        var histMod = SweepFindings.Histogram(byMod);
        var filterNote = SweepFindings.FilterNote(
            wholeOrder ? null : SweepFindings.ScopedCountsClaim,
            recordScope?.Label,
            scope is { Count: > 0 } ? $"plugins=[{string.Join(", ", targets.Concat(offOrderScanned))}]" : null,
            Describe(classes),
            // Stated whenever the caller PASSED an exclusion, zero included: an exclude= that leaves no trace in the
            // response reads as one that was ignored.
            exclude is not null ? $"exclude= left out {excludedFromScope} plugin(s)" : null);

        return new FaceGenCheckResult(findings, npcsScanned, templated, filesSeen, totalFound, noPole,
                                      histClass, histMod, countsOnly, view.ExcludedPlugins, null,
                                      offOrderScanned, filterNote, classes, view.Epoch, limit, scanError,
                                      assets.ReadIncomplete, wholeOrder, noFaceGenRace, raceUnresolved, withheld);

        // ---- the per-NPC join ---------------------------------------------------------------------
        void Classify(FormKey fk, IMajorRecordGetter body, string winnerPlugin, bool offOrderFile)
        {
            var meshPath = FaceGenPath.For(fk, FaceGenSlot.Mesh);
            var tintPath = FaceGenPath.For(fk, FaceGenSlot.Tint);
            var mesh = Half(meshPath);
            var tint = Half(tintPath);
            string master = fk.ModKey.FileName.String;

            FaceGenFindingClass cls;
            string? detail = null;
            if (mesh is { } mw && tint is { } tw)
            {
                if (string.Equals(mw.Layer, tw.Layer, StringComparison.OrdinalIgnoreCase))
                {
                    // A clean pair: both halves come out of ONE MO2 layer. The layer, not the provider name, is
                    // what makes a pair clean - vanilla ships the head in Skyrim - Meshes0.bsa and the tint in
                    // Skyrim - Textures0.bsa, two archives of one product, and a mod's loose file over its own
                    // archive is one mod too. The remaining question is the RECORD one: does the winning record
                    // still agree with the plugin whose mod baked these files?
                    var (stale, why, pole) = StaleAgainstOwner(fk, body, winnerPlugin, mw.Layer);
                    if (pole is null) { noPole++; return; }
                    if (!stale) return;
                    cls = FaceGenFindingClass.StaleBake;
                    detail = why;
                }
                else cls = OneProduct(mw.Layer, tw.Layer) ? FaceGenFindingClass.FamilySplit
                                                         : FaceGenFindingClass.SplitBake;
            }
            else if (mesh is not null) cls = FaceGenFindingClass.TintAbsent;
            else if (tint is not null) cls = FaceGenFindingClass.MeshAbsent;
            else cls = FaceGenFindingClass.BakeAbsent;

            Add(new FaceGenFinding(fk.ToString(), body.EditorID, master,
                                   winnerPlugin + (offOrderFile ? " (off-order)" : ""),
                                   WinnerText(mesh), WinnerText(tint), Token(cls), Fix(cls), detail,
                                   (mesh ?? tint)?.Layer));
        }

        // One half of the pair: the winning provider plus the MO2 LAYER it physically lives in. A loose provider IS
        // its layer; a BSA carries its layer down from the source the resolver actually picked, so a layer this
        // sweep classifies on can never belong to a different archive that happens to share the filename.
        FaceGenHalf? Half(string relPath)
        {
            var hit = assets.Resolve(relPath);
            if (hit is not { Exists: true, Winner: { } w }) return null;
            return new FaceGenHalf(w.Source, w.Kind, w.OwningMod is { Length: > 0 } mod ? mod : w.Source);
        }

        // Two layers of ONE product, as far as their NAMES can say. This is a heuristic and the response says so:
        // an update folder beside its base folder is structurally identical to a genuine cross-bake, and the only
        // signal available at the data layer is the folder names.
        static bool OneProduct(string a, string b)
        {
            var x = Normalize(a);
            var y = Normalize(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;                                       // the same mod, loose over its own BSA
            var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
            // A boundary prefix only: "kids in nirn" covers "kids in nirn - update", "bijin" does not cover
            // "bijin warmaidens" as one product any more than any other shared first word would.
            return shorter.Length >= 6 && longer.StartsWith(shorter, StringComparison.Ordinal)
                && longer.Length > shorter.Length && !char.IsLetterOrDigit(longer[shorter.Length]);
        }

        static string Normalize(string s)
        {
            var t = s.Trim();
            foreach (var suffix in new[] { ".bsa", ".ba2" })
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) t = t[..^suffix.Length];
            return t.ToLowerInvariant();
        }

        // The record axis, run only on a clean pair: compare the WINNING record's face fields against the same
        // record as the FACEGEN OWNER's plugin defines it. The owner's plugin is the pole because the bake was made
        // from it; `previous_provider` is the plugin one step below the winner, which is a different question and
        // was ~25% false on the measured order.
        (bool Stale, string? Why, string? Pole) StaleAgainstOwner(FormKey fk, IMajorRecordGetter winnerBody,
                                                                 string winnerPlugin, string layer)
        {
            var shipped = pluginsInMod(layer);
            if (shipped.Count == 0) return (false, null, null);
            // The pole is a plugin that both ships in the bake's own layer AND actually touches this record. Asking
            // the touching list first is what keeps a whole-order sweep affordable: a mod folder can hold dozens of
            // plugins, and opening each to find it does not define this NPC is a read per plugin per NPC.
            var touching = view.TouchingPlugins(fk);
            if (touching is null) return (false, null, null);
            string? pole = null;
            foreach (var name in touching)                                  // priority order, so the last match wins
            {
                if (!shipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                // The bake's own layer ships the winner: record and files agree by construction, no read needed.
                if (string.Equals(name, winnerPlugin, StringComparison.OrdinalIgnoreCase)) return (false, null, name);
                pole = name;
            }
            if (pole is null) return (false, null, null);

            var ownerBody = view.GetRecord(session, pole, fk, typeof(INpcGetter));
            if (ownerBody is null) return (false, null, null);
            var theirs = ReadEngine.ReadFields(ownerBody, FaceFields, depth: 4);
            var winner = ReadEngine.ReadFields(winnerBody, FaceFields, depth: 4);
            var diff = FieldsDiff.Compare(theirs, winner, referenceLabel: "winner");
            // Two deltas are not a stale bake. HairColor is applied at runtime and the baked files do not carry it;
            // a list whose items are the SAME but in a different ORDER is a subrecord-ordering difference, and the
            // Creation Kit bakes from the values, not from the order it wrote them in.
            var real = diff.Deltas
                           .Where(d => !d.StartsWith(HairColorPath, StringComparison.Ordinal)
                                    && !d.Contains(OrderOnlyDelta, StringComparison.Ordinal))
                           .ToList();
            if (real.Count == 0) return (false, null, pole);
            return (true, $"winner {winnerPlugin} disagrees with the bake's own plugin {pole} on "
                        + string.Join(", ", real.Take(3)) + (real.Count > 3 ? $" (+{real.Count - 3} more)" : ""),
                    pole);
        }

        static string? WinnerText(FaceGenHalf? h)
            => h is null ? null
             : h.Provider + (h.Kind == AssetKind.Loose ? " (loose)" : " (BSA)")
             + (string.Equals(h.Provider, h.Layer, StringComparison.OrdinalIgnoreCase) ? "" : " in " + h.Layer);

        void Emit(FaceGenFindingClass cls, FormKey? fk, string master, string? winner, string? mesh, string? tint,
                  string detail)
        {
            if (!reported.Add(cls + "|" + (fk?.ToString() ?? detail))) return;
            Add(new FaceGenFinding(fk?.ToString(), null, master, winner, mesh, tint, Token(cls), Fix(cls), detail, null));
        }

        void Add(FaceGenFinding f)
        {
            var cls = ClassOf(f.Class);
            if (!classes.HasFlag(cls)) return;
            totalFound++;
            byClass[f.Class] = byClass.GetValueOrDefault(f.Class) + 1;
            if (f.OwningMod is { Length: > 0 } m) byMod[m] = byMod.GetValueOrDefault(m) + 1;
            // The benign class is COUNTED in the header and LISTED only when the caller named it. On the measured
            // order 268 of 408 pair mismatches were one product's two halves, and listing them by default buries
            // the 126 that are real. Held aside rather than dropped: a to_file= artifact carries every class, so
            // "complete findings" stays true there while the response stays readable.
            if (cls == FaceGenFindingClass.FamilySplit && !listFamilySplit)
            {
                if (!countsOnly && withheld.Count < limit) withheld.Add(f);
                return;
            }
            if (countsOnly || findings.Count >= limit) return;
            findings.Add(f);
        }
    }

    /// <summary>Does this NPC inherit its appearance rather than carry a bake of its own? A <c>Template</c> with the
    /// <c>Traits</c> flag means the keyed path is never read for it, so it is EXCLUDED from the population — never
    /// reported as a missing bake, which is what made ~a quarter of the measured record-axis flags false.</summary>
    public static bool InheritsAppearance(INpcGetter npc)
        => !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);

    /// <summary>A record's catalog type name, for the sentence that says what a facegen key actually resolved to.
    /// Read the same way <see cref="ReadEngine"/> reads it, so the two cannot spell one type differently.</summary>
    static string TypeNameOf(IMajorRecordGetter body)
        => RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(body.GetType())?.Name ?? "record");

    /// <summary>Parse a facegen filename into its load-order index byte and 6-hex local id, or null when the name
    /// is not the eight-hex form a bake is keyed by.</summary>
    public static (byte Index, uint Local)? ParseKeyFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length != 8) return null;
        foreach (var c in stem) if (!Uri.IsHexDigit(c)) return null;
        return ((byte)Convert.ToInt32(stem[..2], 16), Convert.ToUInt32(stem[2..], 16));
    }

    /// <summary>The class's token as a caller spells it in <c>findings=</c>.</summary>
    public static string Token(FaceGenFindingClass c) => c switch
    {
        FaceGenFindingClass.TintAbsent => "tint_absent",
        FaceGenFindingClass.MeshAbsent => "mesh_absent",
        FaceGenFindingClass.BakeAbsent => "bake_absent",
        FaceGenFindingClass.SplitBake => "split_bake",
        FaceGenFindingClass.StaleBake => "stale_bake",
        FaceGenFindingClass.FamilySplit => "family_split",
        FaceGenFindingClass.ForeignIndex => "foreign_index",
        FaceGenFindingClass.Inert => "inert",
        _ => c.ToString().ToLowerInvariant(),
    };

    /// <summary>The class one token names, or null.</summary>
    public static FaceGenFindingClass? ClassFor(string token)
    {
        foreach (var c in Registered) if (Token(c) == token) return c;
        return null;
    }

    static FaceGenFindingClass ClassOf(string token) => ClassFor(token) ?? FaceGenFindingClass.None;

    /// <summary>Every class this family reports, in the order a response renders them.</summary>
    public static readonly IReadOnlyList<FaceGenFindingClass> Registered = new[]
    {
        FaceGenFindingClass.TintAbsent, FaceGenFindingClass.MeshAbsent, FaceGenFindingClass.BakeAbsent,
        FaceGenFindingClass.SplitBake, FaceGenFindingClass.StaleBake, FaceGenFindingClass.FamilySplit,
        FaceGenFindingClass.ForeignIndex, FaceGenFindingClass.Inert,
    };

    /// <summary>One fix sentence per class — what to DO about a row, in the row itself.</summary>
    public static string Fix(FaceGenFindingClass c) => c switch
    {
        FaceGenFindingClass.TintAbsent => "Place the pair from the mesh's source; if the tint exists nowhere, re-bake in the Creation Kit (Ctrl+F4).",
        FaceGenFindingClass.MeshAbsent => "Place the pair from the tint's source.",
        FaceGenFindingClass.BakeAbsent => "This NPC needs a bake and has neither half anywhere — re-bake in the Creation Kit (Ctrl+F4).",
        FaceGenFindingClass.SplitBake => "Re-place BOTH halves from one source.",
        FaceGenFindingClass.StaleBake => "Forward the appearance from the facegen owner's plugin, or re-bake.",
        FaceGenFindingClass.FamilySplit => "Usually benign — one product's two halves. Verify only if this NPC renders wrong.",
        FaceGenFindingClass.ForeignIndex => "The bake is keyed to another load order's index byte — rename it to the canonical 00-prefixed name, or re-bake.",
        FaceGenFindingClass.Inert => "Not a face bug — no actor reads this path.",
        _ => "",
    };

    /// <summary>The applied class filter spelled for the render, or null when every class is included.</summary>
    public static string? Describe(FaceGenFindingClass c)
        => c == FaceGenFindingClass.All ? null
         : "findings=[" + string.Join(", ", Registered.Where(r => c.HasFlag(r)).Select(Token)) + "]";

    /// <summary>The whole class vocabulary, for a refusal.</summary>
    public static string Vocabulary => string.Join(", ", Registered.Select(c => "'" + Token(c) + "'"));
}

/// <summary>One half of an NPC's bake as the VFS resolves it: the winning provider, whether it is loose or in an
/// archive, and the MO2 <paramref name="Layer"/> that provider physically lives in — a mod folder, "overwrite" or
/// "Data". The layer is what decides whether two halves are one bake: vanilla splits the head and the tint across
/// two archives of one product, and a mod's loose file over its own archive is still one mod.</summary>
public sealed record FaceGenHalf(string Provider, AssetKind Kind, string Layer);

/// <summary>The facegen family's finding classes. <see cref="FamilySplit"/> is BENIGN and is counted in the header
/// but listed only under its own class token: on the measured order 268 of 408 pair mismatches were one product's
/// two halves, and listing them by default drowns the 126 real ones.</summary>
[Flags]
public enum FaceGenFindingClass
{
    None = 0,
    /// <summary>The mesh wins; the tint has no provider anywhere.</summary>
    TintAbsent = 1,
    /// <summary>The tint wins; the mesh has no provider anywhere.</summary>
    MeshAbsent = 2,
    /// <summary>The NPC needs a bake and NEITHER half exists anywhere in the order.</summary>
    BakeAbsent = 4,
    /// <summary>Both halves win, from different products — the head from one mod, the tint from another.</summary>
    SplitBake = 8,
    /// <summary>A clean same-source pair whose WINNING record disagrees with the facegen owner's plugin on the face
    /// fields (HairColor alone excepted — it does not touch the bake).</summary>
    StaleBake = 16,
    /// <summary>Both halves win, from different mods of ONE product or a repack of its own archive. Benign by
    /// default and named as a name-based inference, not a verdict.</summary>
    FamilySplit = 32,
    /// <summary>A same-local-id file carrying a different load-order index byte in the same folder — a bake keyed to
    /// somebody else's order. Inferred from the file itself, not from the Creation Kit.</summary>
    ForeignIndex = 64,
    /// <summary>The key resolves to a placed reference, to no record at all, or the folder names a plugin not in
    /// this order, or the filename is malformed. Named and dropped; not a face bug.</summary>
    Inert = 128,
    All = TintAbsent | MeshAbsent | BakeAbsent | SplitBake | StaleBake | FamilySplit | ForeignIndex | Inert,
}

/// <summary>One row: one NPC (or one orphaned file), with the three winners that decide its face and the class the
/// join puts it in. <paramref name="FormId"/> is null only for a file whose name carries no readable FormID.</summary>
public sealed record FaceGenFinding(string? FormId, string? EditorId, string DefiningMaster, string? RecordWinner,
                                    string? MeshWinner, string? TintWinner, string Class, string Fix,
                                    string? Detail, string? OwningMod);

/// <summary>The result of <see cref="FaceGenCheck.Run"/>.
/// <para><paramref name="NoComparisonPole"/> is how many clean pairs the stale-bake test could not run on, because
/// the facegen owner's mod ships no plugin that defines the NPC (a texture-only mod, overwrite, Data). Reported
/// rather than folded into "clean": a test that did not run must not read as a test that passed.</para></summary>
public sealed record FaceGenCheckResult(
    IReadOnlyList<FaceGenFinding> Findings,
    int NpcsScanned,
    int NpcsTemplated,
    int FilesSeen,
    int TotalFound,
    int NoComparisonPole,
    IReadOnlyList<SweepCount>? ByClass,
    IReadOnlyList<SweepCount>? ByOwningMod,
    bool CountsOnly,
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Error,
    IReadOnlyList<string>? OffOrderScanned = null,
    string? FilterNote = null,
    FaceGenFindingClass Classes = FaceGenFindingClass.All,
    string? Epoch = null,
    int Limit = 0,
    string? ScanError = null,
    bool ReadIncomplete = false,
    bool WholeOrder = false,
    int NpcsNoFaceGenRace = 0,
    int NpcsRaceUnresolved = 0,
    IReadOnlyList<FaceGenFinding>? WithheldBenign = null)
{
    public bool Success => Error is null;

    /// <summary>Whether this sweep LISTS its benign 'family_split' rows, rather than counting them and withholding
    /// them. True only where the caller named the class: the default sweep asks for every class and gets the rows
    /// held back. One spelling, read by every sentence and field that turns on it, so the transports cannot
    /// disagree about it — the condition <see cref="FaceGenCheck.Run"/> withholds by.</summary>
    public bool FamilySplitListed
        => Classes != FaceGenFindingClass.All && Classes.HasFlag(FaceGenFindingClass.FamilySplit);

    /// <summary>How many findings were ELIGIBLE for the listing — the found total minus the benign class this sweep
    /// counted but did not list. The budget sentence compares against this, so a withheld benign row cannot make a
    /// complete listing claim the listing budget ran out.</summary>
    public int ListableFound
        => FamilySplitListed
           ? TotalFound                                              // the caller named the benign class: it is listed
           : TotalFound - CountOf(FaceGenFindingClass.FamilySplit);

    /// <summary>How many findings of one class this sweep FOUND (never the capped listing's count).</summary>
    public int CountOf(FaceGenFindingClass c)
        => ByClass?.FirstOrDefault(r => r.Key == FaceGenCheck.Token(c))?.Count
        ?? Findings.Count(f => f.Class == FaceGenCheck.Token(c));

    public static FaceGenCheckResult Fail(string error) =>
        new(Array.Empty<FaceGenFinding>(), 0, 0, 0, 0, 0, null, null, false,
            new Dictionary<string, string>(), error);
}

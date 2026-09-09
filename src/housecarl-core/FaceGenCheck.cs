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
        var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var byMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalFound = 0, npcsScanned = 0, templated = 0, noPole = 0;
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
        if (exclude is { } ex)
        {
            var drop = new HashSet<string>(ex.Names, StringComparer.OrdinalIgnoreCase);
            targets.RemoveAll(drop.Contains);
            if (offOrder is { Count: > 0 }) offOrder = offOrder.Where(o => !drop.Contains(o.Name)).ToList();
            if (scope is { Count: > 0 } && targets.Count == 0 && (offOrder is null || offOrder.Count == 0))
                return FaceGenCheckResult.Fail(
                    "exclude= removed every plugin this facegen sweep would have covered — there is nothing left to "
                    + "check. Narrow exclude=, or widen plugins=.") with { Epoch = view.Epoch };
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
            if (body is INpcGetter npc && InheritsAppearance(npc)) { templated++; return; }
            Classify(fk, body, winnerPlugin, offOrderFile);
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
                    Judge(fk, body, view.ResolveWinner(fk)?.WinnerPlugin ?? fk.ModKey.FileName.String, offOrderFile: false);
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
        // Only meaningful over the WHOLE order: under a plugin scope a file for an NPC outside the scope is not
        // inert, it is simply out of scope, and reporting it would be a claim about records this call never read.
        bool wholeOrder = scope is not { Count: > 0 } && (offOrder is null || offOrder.Count == 0);
        if (wholeOrder)
        {
            var seenLocals = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
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
            foreach (var (folder, name, _) in files)
                if (ParseKeyFile(name) is { } p && p.Index == 0)
                {
                    if (!seenLocals.TryGetValue(folder, out var set)) seenLocals[folder] = set = new HashSet<uint>();
                    set.Add(p.Local);
                }

            foreach (var (folder, name, _) in files)
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
                    if (!seenLocals.TryGetValue(folder, out var set) || !set.Contains(local))
                        Emit(FaceGenFindingClass.ForeignIndex, fk, folder, null, null, null,
                             $"{folder}\\{name} — index byte {index:X2}, not the canonical 00; no 00{local:X6} file exists here");
                    continue;
                }
                if (judged.Contains(fk)) continue;                         // answered by the record half above
                if (view.ResolveWinner(fk) is not { } w)
                {
                    Emit(FaceGenFindingClass.Inert, fk, folder, null, null, null,
                         "no plugin in this order defines this FormID, so no NPC reads this bake");
                    continue;
                }
                // Resolves to something that is not an NPC at all — the PlacedNpc case both gate arms got wrong.
                var body = view.GetRecord(session, w.WinnerPlugin, fk);
                if (body is not INpcGetter)
                    Emit(FaceGenFindingClass.Inert, fk, folder, w.WinnerPlugin, null, null,
                         $"this FormID is a {TypeNameOf(body)}, not an NPC — no actor reads this bake");
            }
        }

        var histClass = SweepFindings.Histogram(byClass);
        var histMod = SweepFindings.Histogram(byMod);
        var filterNote = SweepFindings.FilterNote(
            recordScope is null && !wholeOrder ? SweepFindings.ScopedCountsClaim
          : recordScope is null ? null : SweepFindings.ScopedCountsClaim,
            recordScope?.Label,
            scope is { Count: > 0 } ? $"plugins=[{string.Join(", ", targets.Concat(offOrderScanned))}]" : null,
            Describe(classes));

        return new FaceGenCheckResult(findings, npcsScanned, templated, filesSeen, totalFound, noPole,
                                      histClass, histMod, countsOnly, view.ExcludedPlugins, null,
                                      offOrderScanned, filterNote, classes, view.Epoch, limit, scanError,
                                      assets.ReadIncomplete, wholeOrder);

        // ---- the per-NPC join ---------------------------------------------------------------------
        void Classify(FormKey fk, IMajorRecordGetter body, string winnerPlugin, bool offOrderFile)
        {
            var meshPath = FaceGenPath.For(fk, FaceGenSlot.Mesh);
            var tintPath = FaceGenPath.For(fk, FaceGenSlot.Tint);
            var mesh = assets.Resolve(meshPath);
            var tint = assets.Resolve(tintPath);
            string master = fk.ModKey.FileName.String;

            FaceGenFindingClass cls;
            string? detail = null;
            if (mesh.Exists && tint.Exists)
            {
                var mw = mesh.Winner!;
                var tw = tint.Winner!;
                if (SameSource(mw, tw))
                {
                    // A clean pair. The remaining question is the RECORD one: does the winning record still agree
                    // with the plugin whose mod baked these files?
                    var (stale, why, pole) = StaleAgainstOwner(fk, body, winnerPlugin, mw);
                    if (pole is null) { noPole++; return; }
                    if (!stale) return;
                    cls = FaceGenFindingClass.StaleBake;
                    detail = why;
                }
                else cls = OneProduct(mw, tw) ? FaceGenFindingClass.FamilySplit : FaceGenFindingClass.SplitBake;
            }
            else if (mesh.Exists) cls = FaceGenFindingClass.TintAbsent;
            else if (tint.Exists) cls = FaceGenFindingClass.MeshAbsent;
            else cls = FaceGenFindingClass.BakeAbsent;

            var winnerName = mesh.Winner?.Source ?? tint.Winner?.Source;
            Add(new FaceGenFinding(fk.ToString(), body.EditorID, master,
                                   winnerPlugin + (offOrderFile ? " (off-order)" : ""),
                                   WinnerText(mesh), WinnerText(tint), Token(cls), Fix(cls), detail,
                                   winnerName));
        }

        // A pair is CLEAN when both halves come from the same provider. A loose half and a BSA half of the same
        // name are not the same provider — the archive is a different artifact from the folder.
        static bool SameSource(AssetProvider a, AssetProvider b)
            => a.Kind == b.Kind && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

        // Two providers of ONE product, as far as their NAMES can say. This is a heuristic and the response says so:
        // a repack of a mod's own archive, or an update folder beside its base folder, is structurally identical to
        // a genuine cross-bake, and the only signal available at the data layer is the provider names.
        static bool OneProduct(AssetProvider a, AssetProvider b)
        {
            var x = Normalize(a.Source);
            var y = Normalize(b.Source);
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
                                                                 string winnerPlugin, AssetProvider provider)
        {
            foreach (var candidate in pluginsInMod(provider.Source))
            {
                if (string.Equals(candidate, winnerPlugin, StringComparison.OrdinalIgnoreCase)) return (false, null, candidate);
                if (!view.ContainsPlugin(candidate)) continue;
                var ownerBody = view.GetRecord(session, candidate, fk, typeof(INpcGetter));
                if (ownerBody is null) continue;
                var theirs = ReadEngine.ReadFields(ownerBody, FaceFields, depth: 4);
                var winner = ReadEngine.ReadFields(winnerBody, FaceFields, depth: 4);
                var diff = FieldsDiff.Compare(theirs, winner, referenceLabel: "winner");
                var real = diff.Deltas.Where(d => !d.StartsWith(HairColorPath, StringComparison.Ordinal)).ToList();
                if (real.Count == 0) return (false, null, candidate);
                return (true, $"winner {winnerPlugin} disagrees with the bake's own plugin {candidate} on "
                            + string.Join(", ", real.Take(3)) + (real.Count > 3 ? $" (+{real.Count - 3} more)" : ""),
                        candidate);
            }
            return (false, null, null);
        }

        static string? WinnerText(AssetHit hit)
            => hit is { Exists: true, Winner: { } w } ? w.Source + (w.Kind == AssetKind.Loose ? " (loose)" : " (BSA)") : null;

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
            // the 126 that are real.
            if (cls == FaceGenFindingClass.FamilySplit && !listFamilySplit) return;
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
    bool WholeOrder = false)
{
    public bool Success => Error is null;

    /// <summary>How many findings of one class this sweep FOUND (never the capped listing's count).</summary>
    public int CountOf(FaceGenFindingClass c)
        => ByClass?.FirstOrDefault(r => r.Key == FaceGenCheck.Token(c))?.Count
        ?? Findings.Count(f => f.Class == FaceGenCheck.Token(c));

    public static FaceGenCheckResult Fail(string error) =>
        new(Array.Empty<FaceGenFinding>(), 0, 0, 0, 0, 0, null, null, false,
            new Dictionary<string, string>(), error);
}

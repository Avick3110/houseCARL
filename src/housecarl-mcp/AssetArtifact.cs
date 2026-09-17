using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary><c>asset_status</c>'s <c>to_file=</c> artifact: one JSONL row per resolved path under the manifest
/// convention <c>records</c> uses, plus the manifest-only render that goes back inline. ONE row shape whichever
/// SELECT filled it, identity column <c>path</c>; a to_file= call resolves the whole selection, so row_count equals
/// total, while an AUTO-SPILL writes the window the call resolved and the marker says so.</summary>
internal static class AssetArtifact
{
    /// <summary>The columns every row carries, in order, written into the manifest so a consumer has the shape without opening a row.</summary>
    internal static readonly string[] RowSchema =
    {
        "path", "formid", "slot", "exists", "winner", "winner_kind", "winner_mod", "provider_count", "providers",
        "ambiguous", "pair_path", "pair_exists", "pair_winner", "pair_winner_kind", "pair_winner_mod", "pair_differs",
        "error",
    };

    /// <summary>Write the artifact for one resolution: the spill to render, or a named error the caller renders
    /// verbatim. <paramref name="order"/> is the fingerprinted build, or null with <paramref name="noEpochBecause"/>
    /// saying why; <paramref name="reason"/> is <c>to_file</c> or <c>ceiling</c>. Never throws for an IO failure.</summary>
    internal static (SpillInfo? Spill, string? Error) Write(AssetStatusData d, ArtifactTarget target, string reason,
                                                           OrderStamp? order,
                                                           IReadOnlyList<KeyValuePair<string, string>> query,
                                                           string? noEpochBecause = null)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var r in d.Results) writer.WriteRow((w, _) => Row(w, r));

        var notes = new List<string>
        {
            "One row per resolved path. 'winner' is the copy the game actually uses; 'winner_kind' is loose or BSA, "
            + "and for a BSA the winner NAMES the archive while 'winner_mod' names the mod folder shipping it.",
            "A 'formid' row was derived from that NPC: the pair's other half is in 'pair_path' with its own winner "
            + "beside it, and 'pair_differs' is true when both halves resolve and come from different MODS — compared "
            + "by 'winner_mod' / 'pair_winner_mod', because vanilla ships every head in Skyrim - Meshes0.bsa and "
            + "every tint in Skyrim - Textures0.bsa, which is two provider names for one product and not a split.",
            "'winner_mod' is the MO2 LAYER the winning copy ships from, and two of its values are not mods: two "
            + "files both installed into the game's own Data folder, or both landing in overwrite, compare as one "
            + "owner and come back pair_differs=false.",
            "'pair_differs' is PROVENANCE, not a class: it covers both a cross-product split and two mod folders of "
            + "one product. housecarl_check findings=[\"facegen\"] is what separates them.",
            "An ABSENT row is authoritative only where the response's read-failure and discovery alarms were empty; "
            + "the manifest's query echo names whether they were.",
        };

        // Said in the FILE as well as beside the spill marker: an artifact re-read months later carries no conversation.
        if (noEpochBecause is not null)
            notes.Add("'epoch' is EMPTY: the load order could not be read for a fingerprint when this was written — "
                      + noEpochBecause + " The rows are unaffected (they are read off the VFS, not off the record "
                      + "index), but nothing here says which build they sit beside — re-run the call once the order "
                      + "reads to stamp one.");

        // The §2.1 coverage stamp, in the field rather than in prose: every row here is read off the VFS while the
        // fingerprint describes the record build.
        var uncovered = new[]
        {
            "the MO2 VFS layer — every winner, provider chain and pair verdict in these rows is read off mod folders "
            + "and archives, which the record-build fingerprint does not describe",
        };

        // The SELECTION's total, not the rows written: a to_file= call resolved everything, and an auto-spilled
        // limit= window is the case where the two must differ.
        var (manifest, err) = writer.Save(target, ToolNames.AssetStatus, query, identity: "path",
                                          RowSchema, sort: "asset_paths= in the order given, then formids= (mesh then tint per NPC), then under= matches sorted",
                                          total: d.Selected, epoch: order?.Epoch ?? "", notes: notes,
                                          epochUncovered: uncovered, excludedPlugins: order?.ExcludedPlugins);
        return err is not null
            ? (null, err)
            : (new SpillInfo(target.Path, manifest!, reason) { EpochUnavailable = noEpochBecause }, null);
    }

    /// <summary>The response a <c>to_file=</c> call renders: the header, the build-level alarms and the selector
    /// notes — the things an ABSENT row in the file depends on — and the manifest. No rows; the rows ARE the file.</summary>
    internal static string RenderManifestOnly(AssetStatusData d, SpillInfo spill, bool json, int cap)
    {
        if (json) return JsonWire.RenderAssetStatusManifestOnly(d, spill, cap);

        var sb = new StringBuilder();
        sb.Append("asset status — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
          .Append("'  (").Append(d.Selected).Append(" path").Append(d.Selected == 1 ? "" : "s").Append(" selected)\n");
        // The alarms an ABSENT row in the FILE depends on, rendered whatever the budget: the file carries no place to state them.
        var room = RenderCap.For(cap, 0);
        BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
        BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
        if (d.SelectorNotes is { Count: > 0 } notes)
        {
            sb.Append("\n[!] under (").Append(notes.Count).Append("):\n");
            BatchRender.AppendLines(sb, notes, "selector(s)", room);
        }
        Artifacts.AppendSpillText(sb, spill);
        return sb.ToString();
    }

    /// <summary>One row, every column present so a consumer can index by name — except <c>error</c>, written ONLY on
    /// a failed row, because its presence is the artifact contract's marker for a row that names no identity.</summary>
    static void Row(Utf8JsonWriter w, AssetPathResult r)
    {
        w.WriteStartObject();
        w.WriteString("path", r.RelPath);
        Str(w, "formid", r.FormId);
        Str(w, "slot", r.Slot is { } s ? FaceGenPath.Token(s) : null);
        if (r.Error is not null)
        {
            // A per-ROW error, never the file's discriminant: the call succeeded and wrote a row that failed.
            w.WriteNull("exists"); w.WriteNull("winner"); w.WriteNull("winner_kind"); w.WriteNull("winner_mod");
            w.WriteNull("provider_count"); w.WriteNull("providers"); w.WriteNull("ambiguous");
            w.WriteNull("pair_path"); w.WriteNull("pair_exists"); w.WriteNull("pair_winner");
            w.WriteNull("pair_winner_kind"); w.WriteNull("pair_winner_mod"); w.WriteNull("pair_differs");
            w.WriteString("error", r.Error);
            w.WriteEndObject();
            return;
        }

        var hit = r.Hit!;
        w.WriteBoolean("exists", hit.Exists);
        Str(w, "winner", hit.Winner?.Source);
        Str(w, "winner_kind", Kind(hit.Winner));
        // The OWNER, not the bare OwningMod: a loose provider carries no OwningMod, so the raw field would put null
        // in both mod columns of every loose-vs-loose split.
        Str(w, "winner_mod", hit.Winner is { } win ? AssetPathResult.Owner(win) : null);
        w.WriteNumber("provider_count", hit.Providers.Count);
        w.WriteStartArray("providers");
        foreach (var p in hit.Providers)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Source);
            w.WriteString("kind", Kind(p)!);
            Str(w, "mod", p.OwningMod);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("ambiguous", hit.Ambiguous);
        Str(w, "pair_path", r.PairPath);
        if (r.PairHit is { } pair)
        {
            w.WriteBoolean("pair_exists", pair.Exists);
            Str(w, "pair_winner", pair.Winner?.Source);
            Str(w, "pair_winner_kind", Kind(pair.Winner));
            Str(w, "pair_winner_mod", pair.Winner is { } pw ? AssetPathResult.Owner(pw) : null);
            // Compared by OWNING MOD, which is why both mod columns are here: two archive names of one product are not a split.
            w.WriteBoolean("pair_differs", r.PairDiffers);
        }
        else
        {
            w.WriteNull("pair_exists"); w.WriteNull("pair_winner"); w.WriteNull("pair_winner_kind");
            w.WriteNull("pair_winner_mod"); w.WriteNull("pair_differs");
        }
        w.WriteEndObject();
    }

    static string? Kind(AssetProvider? p) => p is null ? null : p.Kind == AssetKind.Bsa ? "BSA" : "loose";

    static void Str(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }
}

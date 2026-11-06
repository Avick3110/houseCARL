using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One record an NPC's face is drawn from, with its FaceGen pair by Mutagen's resolved link, or the reason
/// Mutagen names none (<see cref="Reason"/> set, both paths null).</summary>
public sealed record FaceRoot(FormKey Root, string? Mesh, string? Tint, string? Reason, FaceGap Gap = FaceGap.None);

/// <summary>Which gate input left a face root with no pair, for a lane that counts them.</summary>
public enum FaceGap { None, NoRace, RaceUnread, NoFaceGenHead, TraitsNoTemplate, UseTemplate, Other }

/// <summary>An NPC's one FaceGen answer (docs/facegen.md): the roots its face is drawn from, each with a pair or a
/// reason; the template chain's errors; and a <see cref="Reason"/> when the key names no NPC at all,
/// <see cref="Undefined"/> when nothing the lane reads defines it.</summary>
public sealed record NpcFaceAnswer(FormKey Npc, IReadOnlyList<FaceRoot> Roots, IReadOnlyList<string> Errors, string? Reason, bool Undefined = false)
{
    /// <summary>An answer with no roots: the key names no NPC, or its record was not read.</summary>
    public static NpcFaceAnswer Fail(FormKey npc, string reason) => new(npc, Array.Empty<FaceRoot>(), Array.Empty<string>(), reason);

    /// <summary>The one sentence a formids=, npc= or place row says when an NPC has no FaceGen pair.</summary>
    public static string NoPair(string why) => "no FaceGen pair — " + why;

    /// <summary>The line a root's pair carries: the template root it is, one of N for a leveled template; null for the NPC's own.</summary>
    public string? NoteFor(FaceRoot r)
        => r.Root == Npc ? null
         : $"the face of template root {FormIdToken.Of(r.Root)}" + (Roots.Count > 1 ? $", one of {Roots.Count}" : "");

    /// <summary>Every reason this answer has no pair for, one sentence each: the record's, a chain error's, a root's.</summary>
    public IEnumerable<string> Reasons()
    {
        if (Reason is not null) { yield return Reason; yield break; }
        foreach (var e in Errors) yield return e;
        foreach (var r in Roots)
            if (r.Reason is { } why) yield return r.Root == Npc ? why : $"template root {FormIdToken.Of(r.Root)}: {why}";
    }
}

/// <summary>The one door every lane asks for an NPC's face (DECISIONS 2026-11-05 (19)): the pair is Mutagen's resolved
/// FaceGen link, asked on each face root as that root is. houseCARL adds only what Mutagen lacks: the Traits template
/// walk (<see cref="FaceRoots"/>) and, where Mutagen names nothing, the first gate input the record
/// leaves unmet, stated once. Records come through the lane's own <c>fetch</c>, and <c>unset</c> says where it looked
/// for a key it finds nothing at; races are kept per door.</summary>
public sealed class NpcFace(Func<FormKey, IMajorRecordGetter?> fetch, string unset = NpcFace.Unset)
{
    public const string Unset = "no plugin in this order defines it";
    const string NoRace = "it names no race";
    public const string TraitsNoTemplate = "Traits flag with no Template: Mutagen names no face";
    public const string UseTemplateFlag = "ACBS flag 0x100 ('Use Template?'): Mutagen names no face for it";

    readonly Dictionary<FormKey, (IAssetLinkCache? Cache, IRaceGetter? Race, string? Why)> _races = new();

    /// <summary>The face of the record at <paramref name="key"/>, whose body the lane read (<paramref name="body"/>,
    /// null when nothing defines it).</summary>
    public NpcFaceAnswer Of(FormKey key, IMajorRecordGetter? body)
    {
        if (body is null) return NpcFaceAnswer.Fail(key, unset) with { Undefined = true };
        if (body is not INpcGetter npc)
            return NpcFaceAnswer.Fail(key, $"it is a {RecordNaming.StripOverlay(body.GetType().Name)}, not an NPC");
        var (roots, errors) = FaceRoots(npc, fetch);
        return new(key, roots.Select(Pair).ToList(), errors, null);
    }

    /// <summary>One root's pair by Mutagen's resolved link over a cache holding only its race, or the reason it has none.</summary>
    FaceRoot Pair(INpcGetter root)
    {
        if (root.Race.IsNull) return new(root.FormKey, null, null, NoRace, FaceGap.NoRace);
        if (!_races.TryGetValue(root.Race.FormKey, out var race)) _races[root.Race.FormKey] = race = RaceOf(root.Race.FormKey);
        if (race.Cache is null) return new(root.FormKey, null, null, race.Why, FaceGap.RaceUnread);
        // A race without FaceGenHead can only yield nothing, so the links are not enumerated for it.
        if (!race.Race!.Flags.HasFlag(Race.Flag.FaceGenHead)) { var (no, noGap) = Unmet(root, race.Race); return new(root.FormKey, null, null, no, noGap); }
        List<string> files;
        try
        {
            files = root.EnumerateAssetLinks(AssetLinkQuery.Resolved, race.Cache)
                        .Select(l => l.DataRelativePath.Path)
                        .Where(p => p.Contains(@"\facegendata\", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) { return new(root.FormKey, null, null, $"its FaceGen link was not resolved: {ex.Message}", FaceGap.Other); }
        var mesh = files.FirstOrDefault(p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase));
        var tint = files.FirstOrDefault(p => p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
        if (mesh is not null && tint is not null) return new(root.FormKey, mesh, tint, null);
        var (why, gap) = Unmet(root, race.Race!);
        return new(root.FormKey, null, null, why, gap);
    }

    /// <summary>The first input of Mutagen's FaceGen gate the root leaves unmet, read off the record.</summary>
    static (string, FaceGap) Unmet(INpcGetter root, IRaceGetter race)
    {
        if (!race.Flags.HasFlag(Race.Flag.FaceGenHead))
            return ($"race {FormIdToken.Of(race.FormKey)} has no FaceGenHead flag: no FaceGen, as the CK bakes none", FaceGap.NoFaceGenHead);
        if (root.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) return (TraitsNoTemplate, FaceGap.TraitsNoTemplate);
        if (root.Configuration.Flags.HasFlag(NpcConfiguration.Flag.UseTemplate)) return (UseTemplateFlag, FaceGap.UseTemplate);
        return ("Mutagen names no FaceGen pair for it", FaceGap.Other);
    }

    /// <summary>A race's link cache and record, or why there is none: not found, not read, or a record of another type.</summary>
    (IAssetLinkCache?, IRaceGetter?, string?) RaceOf(FormKey fk)
    {
        IMajorRecordGetter? got;
        try { got = fetch(fk); }
        catch (Exception ex) { return (null, null, $"its race {FormIdToken.Of(fk)} was not read: {ex.Message}"); }
        return got switch
        {
            IRaceGetter r => (RaceCache(r), r, null),
            null => (null, null, $"its race {FormIdToken.Of(fk)} was not read: no plugin in this order or the record sources defines it"),
            _ => (null, null, $"its race link {FormIdToken.Of(fk)} names a {RecordNaming.StripOverlay(got.GetType().Name)}, not a race"),
        };
    }

    /// <summary>A cap on a Traits template chain, far past any real one, so a malformed order cannot spin.</summary>
    public const int TemplateChainCap = 64;

    /// <summary>The NPCs whose FaceGen an NPC's face is drawn from: itself with the Traits template flag off, else the
    /// first NPC down its Template chain with it off, every member of a leveled-list template once each, through nested
    /// leveled lists. A loop, a link that does not resolve, or a record <paramref name="fetch"/> throws on is an error
    /// naming the chain; the other members still count. A record walk, which Mutagen's asset links do not do.</summary>
    public static (IReadOnlyList<INpcGetter> Roots, IReadOnlyList<string> Errors) FaceRoots(INpcGetter npc, Func<FormKey, IMajorRecordGetter?> fetch)
    {
        var roots = new List<INpcGetter>();
        var errors = new List<string>();
        void Follow(FormKey next, List<string> chain)
        {
            var link = chain.Append(FormIdToken.Of(next)).ToList();
            if (chain.Count >= TemplateChainCap || chain.Contains(FormIdToken.Of(next)))
            { errors.Add($"the Traits template chain loops: {string.Join(" → ", link)}"); return; }
            IMajorRecordGetter? body;
            try { body = fetch(next); }
            catch (Exception ex) { errors.Add($"a record in the Traits template chain was not read: {string.Join(" → ", link)} — {ex.Message}"); return; }
            if (body is INpcGetter t) { Walk(t, link); return; }
            if (body is ILeveledNpcGetter)
            {
                foreach (var r in TemplateHops(body)) Follow(r, link);
                return;
            }
            errors.Add($"the Traits template chain ends on a record that does not resolve to an NPC or a leveled list: {string.Join(" → ", link)}");
        }
        void Walk(INpcGetter n, List<string> chain)
        {
            if (TemplateHops(n).FirstOrDefault() is { IsNull: false } next) { Follow(next, chain); return; }
            if (!roots.Any(r => r.FormKey == n.FormKey)) roots.Add(n);
        }
        Walk(npc, new List<string> { FormIdToken.Of(npc.FormKey) });
        return (roots, errors);
    }

    /// <summary>Whether the NPC's face is a template root's: the Traits flag with a Template set.</summary>
    public static bool DrawsFromTemplate(INpcGetter n)
        => n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !n.Template.IsNull;

    /// <summary>The next hop of a Traits template chain from <paramref name="body"/>: an NPC's Template when its Traits
    /// flag is set, every member of a leveled list; none for anything else.</summary>
    static IEnumerable<FormKey> TemplateHops(IMajorRecordGetter? body)
    {
        if (body is INpcGetter n)
        {
            if (DrawsFromTemplate(n)) yield return n.Template.FormKey;
        }
        else if (body is ILeveledNpcGetter lv)
            foreach (var e in lv.Entries ?? Array.Empty<ILeveledNpcEntryGetter>())
                if (e.Data?.Reference is { IsNull: false } r) yield return r.FormKey;
    }

    /// <summary>An asset-link cache holding only <paramref name="race"/>, which is all the FaceGen link resolves.</summary>
    public static IAssetLinkCache RaceCache(IRaceGetter race)
    {
        var mod = new SkyrimMod(race.FormKey.ModKey, SkyrimRelease.SkyrimSE);
        mod.Races.Add(race.DeepCopy());
        return mod.ToImmutableLinkCache().CreateImmutableAssetLinkCache();
    }

    /// <summary>Every record the door will ask for <paramref name="npcs"/>, read a round per hop: the door runs over what
    /// <paramref name="peek"/> knows, and every key it asks that is not known yet goes to <paramref name="read"/> as the
    /// next round, so the templates and races are in hand before <see cref="Of"/> asks.</summary>
    public static void Prefetch(IEnumerable<INpcGetter> npcs, Func<FormKey, (bool Known, IMajorRecordGetter? Body)> peek,
                                Action<IReadOnlyCollection<FormKey>> read)
    {
        var pending = npcs.ToList();
        while (pending.Count > 0)
        {
            var round = new HashSet<FormKey>();
            var unread = new List<INpcGetter>();
            foreach (var npc in pending)
            {
                bool asked = false;
                if (!npc.Race.IsNull && !peek(npc.Race.FormKey).Known) round.Add(npc.Race.FormKey);
                FaceRoots(npc, k =>
                {
                    var (known, body) = peek(k);
                    if (!known) { round.Add(k); asked = true; return null; }
                    if (body is INpcGetter { Race.IsNull: false } n && !peek(n.Race.FormKey).Known) round.Add(n.Race.FormKey);
                    return body;
                });
                if (asked) unread.Add(npc);
            }
            if (round.Count > 0) read(round);
            pending = unread;
        }
    }
}

/// <summary>Records read a round of keys at a time and kept by key: a key read as null stays null; one whose read threw
/// throws with that cause each time it is asked.</summary>
public sealed class RoundReads(Func<IReadOnlyCollection<FormKey>, Func<FormKey, IMajorRecordGetter?>> round)
{
    readonly Dictionary<FormKey, (IMajorRecordGetter? Body, Exception? Error)> _read = new();

    public void Read(IEnumerable<FormKey> keys)
    {
        var want = keys.Where(k => !_read.ContainsKey(k)).Distinct().ToList();
        if (want.Count == 0) return;
        Func<FormKey, IMajorRecordGetter?> got;
        try { got = round(want); }
        catch (Exception ex) { foreach (var k in want) _read[k] = (null, ex); return; }
        foreach (var k in want)
            try { _read[k] = (got(k), null); }
            catch (Exception ex) { _read[k] = (null, ex); }
    }

    public (bool Known, IMajorRecordGetter? Body) Peek(FormKey fk)
        => _read.TryGetValue(fk, out var r) ? (true, r.Body) : (false, null);

    /// <summary>One key's body, read as a round of one if no round has read it.</summary>
    public IMajorRecordGetter? Get(FormKey fk)
    {
        if (!_read.ContainsKey(fk)) Read(new[] { fk });
        var (body, error) = _read[fk];
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return body;
    }

    /// <summary>The NPCs' face inputs (templates, races) read in rounds, through <see cref="NpcFace.Prefetch"/>.</summary>
    public void FaceInputs(IEnumerable<INpcGetter> npcs) => NpcFace.Prefetch(npcs, Peek, Read);
}

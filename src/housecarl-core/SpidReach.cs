using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using R = HousecarlCore.ReachFields.NpcReader;

namespace HousecarlCore;

/// <summary>Which NPC records one SPID line reaches, by SPID 7.3's own filter rules (7.3.0 and 7.3.3 filter alike;
/// rules and sources: docs/architecture/record-reach.md): strings, forms, level, traits and chance, each NPC read through
/// its template chain for the categories it inherits, after SPID's Keyword lines are replayed. Each NPC record is yes,
/// no, not decidable from the files, or decided only in the running game.</summary>
public static class SpidReach
{
    /// <summary>The PotentialFollowerFaction SPID's teammate trait reads (LookupNPC.cpp:72-73).</summary>
    static readonly FormKey FollowerFaction = FormKey.Factory("05C84D:Skyrim.esm");
    static readonly FormKey Player = FormKey.Factory("000007:Skyrim.esm");
    static readonly R[] Readers = Enum.GetValues<R>();
    static readonly Skill[] Skills = Enum.GetValues<Skill>();

    // ---- the records ------------------------------------------------------------------------

    /// <summary>One NPC record's own data, as the filters read it.</summary>
    sealed class Npc
    {
        public FormKey Key; public string? Eid; public string? Name;
        public string[] Kw = []; public FormKey Race; public (FormKey F, int Rank)[] Factions = [];
        public FormKey Class, CombatStyle, Outfit, Voice, Skin;
        public HashSet<FormKey> Spells = new(), Perks = new();
        public FormKey? Template; public NpcConfiguration.TemplateFlag Flags; public NpcConfiguration.Flag CFlags;
        public bool PcMult; public short Level, CalcMin, CalcMax; public byte[] Skills = [];
        public string? Fault;
    }

    sealed class RaceFact { public string[] Kw = []; public string Eid = ""; public bool Child; }

    /// <summary>The NPC each category is read from (null: not decidable, with why), and the IDs SPID matches.</summary>
    sealed class Eff
    {
        public Npc Self = null!;
        public readonly Npc?[] Prov = new Npc?[Readers.Length];
        public readonly string?[] Why = new string?[Readers.Length];
        public FormKey? TemplateKey;
        public HashSet<string> Strings = null!;     // every exact string channel, lower case
        public string? StringsWhy;                  // a channel the files cannot read
        public Dictionary<string, Verdict> Added = new(StringComparer.OrdinalIgnoreCase);   // keywords SPID's Keyword lines give
    }

    /// <summary>One line, compiled once.</summary>
    sealed class Line
    {
        public List<string> SAll = new(), SNot = new(), SMatch = new(), SAny = new();
        public List<Term> FAll = new(), FNot = new(), FMatch = new();
        public ushort LMin, LMax = ushort.MaxValue; public bool LevelSet;
        public List<(int Skill, byte Min, byte Max)> Skills = new(), Weights = new();
        public bool? Female, Unique, Summonable, Child, Leveled, Teammate, Dead;
        public string? ChanceText;
        public string? ValueKeyword; public FormKey? Value;
        public bool ReadsStrings => SAll.Count + SNot.Count + SMatch.Count + SAny.Count > 0;
    }

    sealed record Term(FormKey? Form, Type? Cls, string? Plugin);

    // ---- the answer ---------------------------------------------------------------------------

    /// <summary>Evaluate <paramref name="asked"/> over every NPC winner: the layer's Keyword lines replayed first, in
    /// SPID's order, so a keyword they give counts. <paramref name="layer"/> is every line of the layer, read.</summary>
    public static ReachResult Evaluate(ReachTerm term, SpidLines.Line asked, SpidLines.LineRead read,
                                       IReadOnlyList<(SpidLines.Line Line, SpidLines.LineRead? Read)> layer,
                                       SpidLines.Context ctx, IReachWorld world, ReachFields fields)
    {
        var result = new ReachResult(term, "Npc");
        if (read.Dropped is { } dropped)
        {
            result.Dropped = true;
            result.Notes.Add($"SPID drops the line ({dropped}), so it reaches no NPC.");
            return result;
        }
        var line = Compile(asked, read, ctx);
        result.Notes.Add("The counts are NPC base records, not placed actors; the player and deleted records are left out.");
        bool npcEids = world.EditorIdGap("Npc") is null;
        if (!npcEids) result.Notes.Add($"NPC EditorIDs are not matched: {world.EditorIdGap("Npc")}.");

        var kwEid = new Dictionary<FormKey, string>();
        foreach (var b in world.Winners(typeof(IKeywordGetter))) if (b.EditorID is { } e) kwEid[b.FormKey] = e.ToLowerInvariant();
        var races = new Dictionary<FormKey, RaceFact>();
        foreach (var b in world.Winners(typeof(IRaceGetter)))
        {
            var r = (IRaceGetter)b;
            try
            {
                races[b.FormKey] = new RaceFact
                {
                    Kw = Eids(r.Keywords, kwEid), Eid = r.EditorID ?? "",
                    Child = r.Flags.HasFlag(Race.Flag.Child),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        var classes = new Dictionary<FormKey, IReadOnlyDictionary<Skill, byte>>();
        if (line.Weights.Count > 0)
            foreach (var b in world.Winners(typeof(IClassGetter)))
                try { classes[b.FormKey] = ((IClassGetter)b).SkillWeights.ToDictionary(kv => kv.Key, kv => kv.Value); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
        var lvlnEid = new Dictionary<FormKey, string>();
        if (npcEids && world.EditorIdGap("LeveledNpc") is null)
            foreach (var b in world.Winners(typeof(ILeveledNpcGetter))) if (b.EditorID is { } e) lvlnEid[b.FormKey] = e.ToLowerInvariant();

        var npcs = new Dictionary<FormKey, Npc>();
        foreach (var b in world.Winners(typeof(INpcGetter)))
        {
            world.Ct.ThrowIfCancellationRequested();
            if (b.FormKey == Player || DeletedRecordRule.HasNoLiveBody(b)) continue;
            npcs[b.FormKey] = Fact((INpcGetter)b, kwEid);
        }

        // The layer's Keyword lines, each compiled once; the asked line's own keyword is left to the asked line.
        var feeders = new List<Line>();
        foreach (var (l, r) in layer)
        {
            if (l.Skipped is not null || l.Kind != SpidLineKind.Regular || l.Type != "Keyword" || r is not { Dropped: null }) continue;
            var c = Compile(l, r, ctx);
            if (c.ValueKeyword is null || (line.ValueKeyword is { } own && own.Equals(c.ValueKeyword, StringComparison.OrdinalIgnoreCase))) continue;
            feeders.Add(c);
        }
        var groups = Groups(layer, line, ctx, kwEid);
        int layerFeeders = feeders.Count;
        feeders = Readable(feeders, line, groups);
        if (feeders.Count > 0)
            result.Notes.Add($"SPID's {feeders.Count:N0} Keyword line(s) of the layer were replayed first, so a keyword they give counts; the other {layerFeeders - feeders.Count:N0} give none it reads.");
        else if (layerFeeders > 0)
            result.Notes.Add($"None of SPID's {layerFeeders:N0} Keyword line(s) of the layer gives a keyword it reads, so none was replayed.");
        if (line.ChanceText is not null)
            result.Notes.Add($"The line's chance is {line.ChanceText} as written: every NPC its filters select is rolled in the running game.");
        if (asked.Kind == SpidLineKind.Death)
            result.Notes.Add("A Death line hands its form out when the NPC dies, in the running game.");

        var lists = new Dictionary<FormKey, IReadOnlyList<FormKey>?>();
        foreach (var n in npcs.Values)
        {
            world.Ct.ThrowIfCancellationRequested();
            var e = Resolve(n, npcs, world, fields, races, lvlnEid, npcEids);
            Replay(feeders, e, races, classes, lists, world);
            var v = Eval(line, e, races, classes, lists, world);
            if (asked.Kind == SpidLineKind.Death && v.V != Reach.No) v = Verdict.And(v, Verdict.Game("a Death line"));
            // An ExclusiveGroup sibling the NPC already holds keeps the line's form off it (LookupNPC.cpp:202-214).
            if (v.V != Reach.No && groups.Count > 0) v = Verdict.And(v, Verdict.Any(groups, s => Sibling(s, e, races, lists, world)).Not());
            result.Add(n.Key, v);
        }
        return result;
    }

    /// <summary>The feeders whose keyword the asked line can read, directly or through another feeder it reads: a
    /// keyword no string of the line names, and no ExclusiveGroup sibling is, changes none of its verdicts.</summary>
    static List<Line> Readable(List<Line> feeders, Line asked, List<Sib> groups)
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var part = new List<string>();
        void Reads(Line l) { exact.UnionWith(l.SAll); exact.UnionWith(l.SNot); exact.UnionWith(l.SMatch); part.AddRange(l.SAny); }
        Reads(asked);
        foreach (var g in groups) if (g.Keyword is { } k) exact.Add(k);
        var kept = new List<Line>();
        var left = new List<Line>(feeders);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var f in left.ToList())
            {
                var k = f.ValueKeyword!;
                if (!exact.Contains(k) && !part.Any(p => k.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                kept.Add(f); left.Remove(f); Reads(f); grew = true;
            }
        }
        // In layer order, as SPID's pass runs them.
        return feeders.Where(kept.Contains).ToList();
    }

    static string[] Eids(IReadOnlyList<IFormLinkGetter<IKeywordGetter>>? kws, Dictionary<FormKey, string> kwEid)
        => kws is null ? [] : kws.Select(k => kwEid.GetValueOrDefault(k.FormKey)).OfType<string>().ToArray();

    static Npc Fact(INpcGetter n, Dictionary<FormKey, string> kwEid)
    {
        var x = new Npc { Key = n.FormKey };
        try
        {
            x.Eid = n.EditorID?.ToLowerInvariant();
            x.Name = n.Name?.String?.ToLowerInvariant();
            x.Kw = Eids(n.Keywords, kwEid);
            x.Race = n.Race.FormKey;
            x.Factions = n.Factions.Select(f => (f.Faction.FormKey, (int)f.Rank)).ToArray();
            x.Class = n.Class.FormKey; x.CombatStyle = n.CombatStyle.FormKey; x.Outfit = n.DefaultOutfit.FormKey;
            x.Voice = n.Voice.FormKey; x.Skin = n.WornArmor.FormKey;
            if (n.ActorEffect is { } ae) foreach (var s in ae) x.Spells.Add(s.FormKey);
            if (n.Perks is { } ps) foreach (var p in ps) x.Perks.Add(p.Perk.FormKey);
            x.Template = n.Template is { IsNull: false } t ? t.FormKey : null;
            var c = n.Configuration;
            x.Flags = c.TemplateFlags; x.CFlags = c.Flags; x.CalcMin = c.CalcMinLevel; x.CalcMax = c.CalcMaxLevel;
            if (c.Level is IPcLevelMultGetter) x.PcMult = true;
            else if (c.Level is INpcLevelGetter l) x.Level = l.Level;
            x.Skills = Skills.Select(s => n.PlayerSkills?.SkillValues.TryGetValue(s, out var v) == true ? v : (byte)0).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { x.Fault = $"its record could not be read ({ex.GetType().Name})"; }
        return x;
    }

    /// <summary>The provider of each category: the NPC itself, or the first NPC up its template chain whose flag for that
    /// category is clear; a chain reaching a leveled actor, a record that does not resolve, or a loop decides nothing.</summary>
    static Eff Resolve(Npc n, Dictionary<FormKey, Npc> npcs, IReachWorld world, ReachFields fields, Dictionary<FormKey, RaceFact> races,
                       Dictionary<FormKey, string> lvlnEid, bool npcEids)
    {
        var e = new Eff { Self = n };
        foreach (var reader in Readers)
        {
            if (n.Template is null && n.Fault is null) { e.Prov[(int)reader] = n; continue; }
            var flag = fields.NpcTemplate[reader];
            var at = n;
            var seen = new HashSet<FormKey> { n.Key };
            string? why = n.Fault;
            while (why is null && at.Template is { } t && at.Flags.HasFlag(flag))
            {
                if (npcs.TryGetValue(t, out var next))
                {
                    if (!seen.Add(t)) { why = "its template chain loops"; break; }
                    if (next.Fault is { } f) { why = "a record up its template chain could not be read"; break; }
                    at = next;
                    continue;
                }
                var cls = world.ClassOf(t);
                why = cls is null ? "its template does not resolve"
                    : typeof(ILeveledNpcGetter).IsAssignableFrom(cls) ? "its template chain ends at a leveled actor"
                    : "its template is no NPC";
            }
            if (why is null) e.Prov[(int)reader] = at; else e.Why[(int)reader] = why;
        }
        string? templateEid = null;
        if (n.Template is { } tk)
        {
            e.TemplateKey = tk;
            if (npcEids) templateEid = npcs.TryGetValue(tk, out var tn) ? tn.Eid : lvlnEid.GetValueOrDefault(tk);
        }
        // The exact string channels: keywords (own and the race's), the name, the NPC's and its template's EditorIDs.
        var s = new HashSet<string>(StringComparer.Ordinal);
        if (npcEids && n.Eid is { } eid) s.Add(eid);
        if (templateEid is { } te) s.Add(te);
        string? sw = null;
        if (P(e, R.Name) is { } nn) { if (nn.Name is { } name) s.Add(name); } else sw ??= e.Why[(int)R.Name];
        if (P(e, R.Keywords) is { } kn) s.UnionWith(kn.Kw); else sw ??= e.Why[(int)R.Keywords];
        if (P(e, R.Race) is { } rn) { if (races.TryGetValue(rn.Race, out var rf)) s.UnionWith(rf.Kw); } else sw ??= e.Why[(int)R.Race];
        e.Strings = s; e.StringsWhy = sw;
        return e;
    }

    static Npc? P(Eff e, R r) => e.Prov[(int)r];
    static Verdict Unknown(Eff e, R r) => Verdict.Undecided(e.Why[(int)r] ?? "unread");

    /// <summary>SPID's Keyword pass: every feeder line over the NPC until no keyword is added; a give the files cannot
    /// settle is kept as open, with its reason, so a later line reading it is open too.</summary>
    static void Replay(List<Line> feeders, Eff e, Dictionary<FormKey, RaceFact> races, Dictionary<FormKey, IReadOnlyDictionary<Skill, byte>> classes,
                       Dictionary<FormKey, IReadOnlyList<FormKey>?> lists, IReachWorld world)
    {
        if (feeders.Count == 0) return;
        bool changed = true;
        bool first = true;
        for (int pass = 0; changed && pass < 16; pass++)
        {
            changed = false;
            foreach (var f in feeders)
            {
                // After the first pass only a line reading strings can see what an earlier give changed.
                if (!first && !f.ReadsStrings) continue;
                var v = Eval(f, e, races, classes, lists, world);
                if (v.V == Reach.No) continue;
                var had = e.Added.TryGetValue(f.ValueKeyword!, out var old);
                if (had && (old.V == Reach.Yes || v.V != Reach.Yes)) continue;
                e.Added[f.ValueKeyword!] = v.V == Reach.Yes ? Verdict.Yes : new Verdict(v.V, $"a Keyword line that gives a keyword it reads ({v.Why})");
                changed = true;
            }
            first = false;
        }
    }

    // ---- one line over one NPC ------------------------------------------------------------------

    static Verdict Eval(Line l, Eff e, Dictionary<FormKey, RaceFact> races, Dictionary<FormKey, IReadOnlyDictionary<Skill, byte>> classes,
                        Dictionary<FormKey, IReadOnlyList<FormKey>?> lists, IReachWorld world)
    {
        var v = Verdict.Yes;
        if (l.ReadsStrings)
        {
            if (l.SAll.Count > 0) v = Verdict.And(v, Verdict.All(l.SAll, s => Str(s, e)));
            if (v.V != Reach.No && l.SNot.Count > 0) v = Verdict.And(v, Verdict.Any(l.SNot, s => Str(s, e)).Not());
            if (v.V != Reach.No && l.SMatch.Count > 0) v = Verdict.And(v, Verdict.Any(l.SMatch, s => Str(s, e)));
            if (v.V != Reach.No && l.SAny.Count > 0) v = Verdict.And(v, Verdict.Any(l.SAny, s => Part(s, e)));
            if (v.V == Reach.No) return v;
        }
        if (l.FAll.Count > 0) v = Verdict.And(v, Verdict.All(l.FAll, t => Form(t, e, races, lists, world)));
        if (v.V != Reach.No && l.FNot.Count > 0) v = Verdict.And(v, Verdict.Any(l.FNot, t => Form(t, e, races, lists, world)).Not());
        if (v.V != Reach.No && l.FMatch.Count > 0) v = Verdict.And(v, Verdict.Any(l.FMatch, t => Form(t, e, races, lists, world)));
        if (v.V == Reach.No) return v;
        v = Verdict.And(v, Level(l, e, classes));
        if (v.V == Reach.No) return v;
        v = Verdict.And(v, Traits(l, e, races));
        if (v.V != Reach.No && l.ChanceText is not null) v = Verdict.And(v, Verdict.Game($"chance {l.ChanceText}"));
        return v;
    }

    /// <summary>An exact string: a keyword's EditorID (the NPC's, its race's, one SPID gave), the name, or an EditorID SPID matches.</summary>
    static Verdict Str(string s, Eff e)
    {
        if (e.Strings.Contains(s)) return Verdict.Yes;
        var v = e.StringsWhy is { } w ? Verdict.Undecided(w) : Verdict.No;
        return e.Added.TryGetValue(s, out var a) ? Verdict.Or(v, a) : v;
    }

    /// <summary>Part of a string: of the name, of an EditorID SPID matches, of a keyword's EditorID.</summary>
    static Verdict Part(string s, Eff e)
    {
        foreach (var x in e.Strings) if (x.Contains(s, StringComparison.Ordinal)) return Verdict.Yes;
        var v = e.StringsWhy is { } w ? Verdict.Undecided(w) : Verdict.No;
        foreach (var (k, a) in e.Added) if (k.Contains(s, StringComparison.OrdinalIgnoreCase)) { v = Verdict.Or(v, a); if (v.V == Reach.Yes) break; }
        return v;
    }

    static Verdict Form(Term t, Eff e, Dictionary<FormKey, RaceFact> races, Dictionary<FormKey, IReadOnlyList<FormKey>?> lists, IReachWorld world)
    {
        if (t.Plugin is { } p)
            return Verdict.Of(e.Self.Key.ModKey.FileName.String.Equals(p, StringComparison.OrdinalIgnoreCase)
                              || e.TemplateKey is { } tk && tk.ModKey.FileName.String.Equals(p, StringComparison.OrdinalIgnoreCase));
        return Has(t.Form!.Value, t.Cls, e, races, lists, world, 0);
    }

    /// <summary>SPID's <c>has_form</c> (LookupNPC.cpp:122-175), each field read off the category that supplies it.</summary>
    static Verdict Has(FormKey f, Type? cls, Eff e, Dictionary<FormKey, RaceFact> races, Dictionary<FormKey, IReadOnlyList<FormKey>?> lists,
                       IReachWorld world, int depth)
    {
        if (cls is null) return Verdict.No;
        Verdict On(R r, Func<Npc, bool> test) => P(e, r) is { } n ? Verdict.Of(test(n)) : Unknown(e, r);
        if (Is<ICombatStyleGetter>(cls)) return On(R.CombatStyle, n => n.CombatStyle == f);
        if (Is<IClassGetter>(cls)) return On(R.Class, n => n.Class == f);
        if (Is<IFactionGetter>(cls)) return On(R.Factions, n => n.Factions.Any(x => x.F == f && x.Rank > -1));
        if (Is<IRaceGetter>(cls)) return On(R.Race, n => n.Race == f);
        if (Is<IOutfitGetter>(cls)) return On(R.Outfit, n => n.Outfit == f);
        if (Is<INpcGetter>(cls)) return Verdict.Of(e.Self.Key == f || e.TemplateKey == f);
        if (Is<IPlacedNpcGetter>(cls)) return Verdict.Game("a placed-actor filter");
        if (Is<IVoiceTypeGetter>(cls)) return On(R.Voice, n => n.Voice == f);
        if (Is<ISpellGetter>(cls)) return On(R.Spells, n => n.Spells.Contains(f));
        if (Is<IArmorGetter>(cls)) return On(R.Skin, n => n.Skin == f);
        if (Is<ILocationGetter>(cls)) return Verdict.Game("a location filter, read off the placed actor");
        if (Is<IPerkGetter>(cls)) return On(R.Perks, n => n.Perks.Contains(f));
        if (Is<IFormListGetter>(cls))
        {
            if (depth > 16) return Verdict.Undecided("a form list that nests too deep");
            if (!lists.TryGetValue(f, out var members)) lists[f] = members = world.ListItems(f);
            if (members is null) return Verdict.No;
            return Verdict.Any(members, m => Has(m, world.ClassOf(m), e, races, lists, world, depth + 1));
        }
        return Verdict.No;
    }

    static bool Is<T>(Type cls) => typeof(T).IsAssignableFrom(cls);

    /// <summary>The actor level, skill levels and skill weights (LookupFilters.cpp:55-145).</summary>
    static Verdict Level(Line l, Eff e, Dictionary<FormKey, IReadOnlyDictionary<Skill, byte>> classes)
    {
        var v = Verdict.Yes;
        if (l.LevelSet)
        {
            if (P(e, R.Level) is not { } s) v = Unknown(e, R.Level);
            else if (s.PcMult)
            {
                int lo = s.CalcMin, hi = s.CalcMax <= 0 ? int.MaxValue : s.CalcMax;
                v = lo >= l.LMin && hi <= l.LMax ? Verdict.Yes
                    : hi < l.LMin || lo > l.LMax ? Verdict.No
                    : Verdict.Undecided("a PC-level-mult NPC's level, which the player's level sets");
            }
            else v = Verdict.Of(s.Level >= l.LMin && s.Level <= l.LMax);
            if (v.V == Reach.No) return v;
        }
        if (l.Skills.Count > 0)
        {
            if (P(e, R.Skills) is not { } s) v = Verdict.And(v, Unknown(e, R.Skills));
            else if (s.CFlags.HasFlag(NpcConfiguration.Flag.AutoCalcStats)) v = Verdict.And(v, Verdict.Undecided("an autocalc NPC's skills, which the engine computes"));
            else foreach (var (sk, min, max) in l.Skills)
                {
                    var val = sk < s.Skills.Length ? s.Skills[sk] : 0;
                    v = Verdict.And(v, Verdict.Of(val >= min && val <= max));
                }
            if (v.V == Reach.No) return v;
        }
        if (l.Weights.Count > 0)
        {
            if (P(e, R.Class) is not { } c) v = Verdict.And(v, Unknown(e, R.Class));
            else if (classes.TryGetValue(c.Class, out var w))
            {
                foreach (var (sk, min, max) in l.Weights)
                {
                    var val = sk < Skills.Length && w.TryGetValue(Skills[sk], out var b) ? b : 0;
                    v = Verdict.And(v, Verdict.Of(val >= min && val <= max));
                }
            }
        }
        return v;
    }

    /// <summary>The traits (LookupFilters.cpp:148-180; LookupNPC.cpp:41-42, :72-73).</summary>
    static Verdict Traits(Line l, Eff e, Dictionary<FormKey, RaceFact> races)
    {
        var v = Verdict.Yes;
        Verdict Flag(R r, bool? want, NpcConfiguration.Flag flag)
            => want is not { } w ? Verdict.Yes : P(e, r) is { } n ? Verdict.Of(n.CFlags.HasFlag(flag) == w) : Unknown(e, r);
        v = Verdict.And(v, Flag(R.Sex, l.Female, NpcConfiguration.Flag.Female));
        v = Verdict.And(v, Flag(R.Unique, l.Unique, NpcConfiguration.Flag.Unique));
        v = Verdict.And(v, Flag(R.Summonable, l.Summonable, NpcConfiguration.Flag.Summonable));
        if (l.Child is { } child)
            v = Verdict.And(v, P(e, R.Race) is { } rn
                ? Verdict.Of((races.TryGetValue(rn.Race, out var rf) && (rf.Child || rf.Eid.Contains("RaceChild", StringComparison.Ordinal))) == child)
                : Unknown(e, R.Race));
        if (l.Leveled is not null) v = Verdict.And(v, Verdict.Game("trait L, whether the actor came from a leveled list"));
        if (l.Teammate is { } mate)
        {
            var inFaction = P(e, R.Factions) is { } fn ? Verdict.Of(fn.Factions.Any(x => x.F == FollowerFaction && x.Rank > -1)) : Unknown(e, R.Factions);
            var t = inFaction.V == Reach.Yes ? Verdict.Yes : Verdict.Or(inFaction, Verdict.Game("trait T, whether the actor is the player's teammate"));
            v = Verdict.And(v, mate ? t : t.Not());
        }
        if (l.Dead is not null) v = Verdict.And(v, Verdict.Game("trait D, whether the actor is dead"));
        return v;
    }

    // ---- compiling a line -----------------------------------------------------------------------

    static Line Compile(SpidLines.Line src, SpidLines.LineRead read, SpidLines.Context ctx)
    {
        var g = ctx.G;
        var l = new Line();
        var filterTypes = g.FilterTypes;
        foreach (var t in src.Tokens)
        {
            if (t.Field == SpidField.Strings)
            {
                var s = t.Raw.ToLowerInvariant();
                (t.List switch { SpidLines.ListKind.All => l.SAll, SpidLines.ListKind.Not => l.SNot, SpidLines.ListKind.Partial => l.SAny, _ => l.SMatch }).Add(s);
            }
            else if (t.Field == SpidField.Forms)
            {
                var r = DistributorLines.Resolve(t.Raw, ctx.Resolver, ctx.S, "SPID");
                Term? term = null;
                if (r.Form is { } fk)
                {
                    var cls = ctx.ClassOf(fk);
                    // A '+' part skips SPID's filter-type check; a plain or '-' one of a type SPID does not take is skipped.
                    if (t.List == SpidLines.ListKind.All || SpidGrammar.IsOneOf(cls, filterTypes)) term = new Term(fk, cls, null);
                }
                else if (r.Plugin is { } p && r.PluginPresent) term = new Term(null, null, p);
                if (term is null) continue;
                (t.List switch { SpidLines.ListKind.All => l.FAll, SpidLines.ListKind.Not => l.FNot, _ => l.FMatch }).Add(term);
            }
            else if (t.Field == SpidField.Value)
            {
                var r = DistributorLines.Resolve(t.Raw, ctx.Resolver, ctx.S, "SPID");
                l.Value = r.Form;
                if (src.Type == "Keyword")
                    l.ValueKeyword = r.Form is { } k ? ctx.Resolver.EditorIdOf(k) ?? t.Raw : t.Raw;
            }
        }
        foreach (var (field, sec) in Sections(src, g))
        {
            switch (field)
            {
                case SpidField.Level:
                    foreach (var part in sec.Split(','))
                    {
                        var p = part.Trim();
                        if (p.Length == 0) continue;
                        if (p.Contains('('))
                        {
                            bool weight = p.StartsWith('w');
                            var nums = new string(p.Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                            if (weight && nums.Count > 0) nums[0] = nums[0][1..];
                            if (nums.Count < 2 || !uint.TryParse(nums[0], out var sk) || sk >= 18) continue;
                            var min = Byte(nums[1]);
                            var max = nums.Count > 2 ? Byte(nums[2]) : (weight ? min : byte.MaxValue);
                            (weight ? l.Weights : l.Skills).Add(((int)sk, min, max));
                        }
                        else
                        {
                            l.LevelSet = true;
                            var mm = p.Split('/');
                            l.LMin = UShort(mm[0]);
                            l.LMax = mm.Length > 1 ? UShort(mm[1]) : ushort.MaxValue;
                        }
                    }
                    break;
                case SpidField.Traits:
                    foreach (var tr in sec.Split('/'))
                        switch (tr.Trim())
                        {
                            case "M": case "-F": l.Female = false; break;
                            case "F": case "-M": l.Female = true; break;
                            case "U": l.Unique = true; break;
                            case "-U": l.Unique = false; break;
                            case "S": l.Summonable = true; break;
                            case "-S": l.Summonable = false; break;
                            case "C": l.Child = true; break;
                            case "-C": l.Child = false; break;
                            case "L": l.Leveled = true; break;
                            case "-L": l.Leveled = false; break;
                            case "T": l.Teammate = true; break;
                            case "-T": l.Teammate = false; break;
                            case "D": l.Dead = true; break;
                            case "-D": l.Dead = false; break;
                        }
                    break;
                case SpidField.Chance:
                    var num = sec.EndsWith('!') ? sec[..^1] : sec;
                    if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var ch) && ch / 100.0 < 1)
                        l.ChanceText = sec;
                    break;
            }
        }
        return l;
    }

    /// <summary>The line's set '|' sections after the value, by field, as SPID splits them after sanitize.</summary>
    static IEnumerable<(SpidField Field, string Section)> Sections(SpidLines.Line src, SpidGrammar g)
    {
        int eq = src.Raw.IndexOf(g.Assign);
        if (eq < 0) yield break;
        var secs = SpidLines.Sanitize(src.Raw[(eq + 1)..].Trim(), g).Split(g.Sections);
        var fields = g.Kind(src.Kind).Fields;
        for (int k = 0; k < secs.Length && k < fields.Count; k++)
        {
            var s = secs[k].Trim();
            if (s.Length > 0 && !s.Equals(g.Unset, StringComparison.OrdinalIgnoreCase)) yield return (fields[k], s);
        }
    }

    // SPID's string::to_num reads the leading number and stops; a bad one reads as zero here.
    static ushort UShort(string s) => ushort.TryParse(s.Trim(), out var v) ? v : (ushort)0;
    static byte Byte(string s) => byte.TryParse(s.Trim(), out var v) ? v : (byte)0;

    /// <summary>One ExclusiveGroup member: a keyword by its EditorID in lower case (one SPID creates included), any other form by its key.</summary>
    sealed record Sib(FormKey? Form, string? Keyword);

    /// <summary>The ExclusiveGroup siblings of the asked line's value: every other member of a group that holds it.</summary>
    static List<Sib> Groups(IReadOnlyList<(SpidLines.Line Line, SpidLines.LineRead? Read)> layer, Line asked, SpidLines.Context ctx,
                            Dictionary<FormKey, string> kwEid)
    {
        var sibs = new List<Sib>();
        var own = asked.ValueKeyword?.ToLowerInvariant();
        if (asked.Value is null && own is null) return sibs;
        foreach (var (l, r) in layer)
        {
            if (l.Kind != SpidLineKind.ExclusiveGroup || l.Skipped is not null || r is null) continue;
            var members = l.Tokens.Where(t => t.Field == SpidField.Members && t.List == SpidLines.ListKind.Plain)
                .Select(t => Member(t.Raw, ctx, kwEid)).OfType<Sib>().ToList();
            bool Mine(Sib m) => m.Keyword is { } k ? k == own : m.Form == asked.Value;
            if (members.Any(Mine)) sibs.AddRange(members.Where(m => !Mine(m)));
        }
        return sibs.Distinct().ToList();
    }

    /// <summary>A group member as SPID resolves it after it creates its keywords: an EditorID no record holds is a created keyword.</summary>
    static Sib? Member(string raw, SpidLines.Context ctx, Dictionary<FormKey, string> kwEid)
    {
        var r = DistributorLines.Resolve(raw, ctx.Resolver, ctx.S, "SPID");
        if (r.Form is { } fk)
            return ctx.ClassOf(fk) is { } cls && Is<IKeywordGetter>(cls)
                ? kwEid.TryGetValue(fk, out var k) ? new Sib(null, k) : null
                : new Sib(fk, null);
        return r.ByEditorId ? new Sib(null, raw.Trim().ToLowerInvariant()) : null;
    }

    /// <summary>Whether the NPC holds a group sibling: a keyword by its EditorID (one SPID gave included), any other form as SPID's has_form.</summary>
    static Verdict Sibling(Sib sib, Eff e, Dictionary<FormKey, RaceFact> races,
                           Dictionary<FormKey, IReadOnlyList<FormKey>?> lists, IReachWorld world)
        => sib.Keyword is { } k ? Str(k, e) : Has(sib.Form!.Value, world.ClassOf(sib.Form.Value), e, races, lists, world, 0);
}

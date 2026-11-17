using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Which item records one KID line reaches, by KID 4.0.6's own rules (rules and sources:
/// docs/architecture/record-reach.md): the line's type, its filters, traits and chance, in KID's own pass order, so a
/// keyword another line of the type gives counts, and Magic Effect lines run first for a magic item's costliest effect.
/// Each item record is yes, no, not decidable from the files, or decided only in the running game.</summary>
public static class KidReach
{
    /// <summary>A keyword as KID holds it: an existing keyword record, or one a KID line creates, by its name in any case.</summary>
    static string Id(FormKey fk) => fk.ToString();
    static string Created(string name) => "+" + name.ToLowerInvariant();

    /// <summary>One filter rule: a form, a plugin, an exact string (plain, model, or an actor-value or archetype name),
    /// or a partial string; a '-' excludes.</summary>
    sealed record Rule(bool Not, FormKey? Form, Type? Cls, string? Keyword, string? Plugin, string? Exact, bool Model, bool Named, string? Part);

    /// <summary>One line, compiled once.</summary>
    sealed class Line
    {
        public string KeywordId = ""; public string KeywordEid = "";
        public List<Rule> Any = new(); public List<List<Rule>> All = new();
        public List<Rule> Excluded = new(), Exact = new(), Partial = new();   // the plain list, split once
        public Func<IMajorRecordGetter, World, Verdict>? Traits;
        public float Chance = 1f; public string? ChanceText;
        public bool ReadsKeywords;
    }

    /// <summary>One item while it is judged: its own keywords, the ones KID adds before the asked line, its strings.</summary>
    sealed class Item
    {
        public IMajorRecordGetter Body = null!; public string Record = "";
        public HashSet<string> Own = new(StringComparer.Ordinal);
        public List<string> OwnEids = new();   // lower case, for partial strings
        public Dictionary<string, Verdict> Added = new(StringComparer.Ordinal);
        public string? Edid; public string Name = ""; public string Model = "";
    }

    /// <summary>What a judged item reads beyond itself, built once per call.</summary>
    sealed class World
    {
        public IReachWorld W = null!; public ReachFields F = null!; public KidGrammar G = null!;
        public Dictionary<FormKey, string> KwEid = new();
        public Dictionary<FormKey, Dictionary<string, Verdict>>? MgefKw;
        public Dictionary<FormKey, int> MgefSkill = new();
        public Dictionary<FormKey, FormKey[]> Effects = new();   // ENCH and SPEL winners' effects
        public Dictionary<FormKey, FormKey?> LocParent = new();
        public Dictionary<FormKey, IReadOnlyList<FormKey>?> Lists = new();
        public bool MgefRead, EffectsRead, LocRead;
    }

    // ---- the answer ---------------------------------------------------------------------------

    /// <summary>Evaluate <paramref name="asked"/> over every winner of its type: the type's other lines first, in KID's
    /// pass order, so a keyword they give counts. <paramref name="layer"/> is every line of the layer, read.</summary>
    public static ReachResult Evaluate(ReachTerm term, KidLines.Line asked, KidLines.LineRead read,
                                       IReadOnlyList<(KidLines.Line Line, KidLines.LineRead? Read)> layer,
                                       KidLines.Context ctx, IReachWorld world, ReachFields fields)
    {
        var g = ctx.G;
        var type = g.Type(asked.Type!)!;
        var result = new ReachResult(term, type.Record);
        if (read.Dropped is { } dropped)
        {
            result.Dropped = true;
            result.Notes.Add($"KID drops the line ({dropped}), so it reaches no item.");
            return result;
        }
        result.Notes.Add("Deleted records are left out.");
        var w = new World { W = world, F = fields, G = g };
        foreach (var b in world.Winners(typeof(IKeywordGetter))) if (b.EditorID is { } e) w.KwEid[b.FormKey] = e;
        var created = new HashSet<string>(layer.Where(x => x.Line is { Skipped: null, IsGroup: false } && x.Read is not null)
            .Select(x => x.Line.Keyword).Where(k => ctx.Resolver.ResolveEditorId(k, g.KeywordRecord) is null && !IsFormId(k, ctx)),
            StringComparer.OrdinalIgnoreCase);
        var line = Compile(asked, ctx, created, w);
        var feeders = Feeders(layer, asked.Type!, line.KeywordId, ctx, created, w);
        // The type's earlier lines of the same keyword: KID gives it by the first line that passes, so an item they tag already holds it.
        var earlier = Earlier(layer, asked, line.KeywordId, ctx, created, w);
        if (earlier.Count > 0)
            result.Notes.Add($"{earlier.Count:N0} earlier {asked.Type} line(s) of the same keyword were run first; an item they tag already holds it.");
        var siblings = Siblings(layer, line.KeywordId, ctx, created);
        int layerFeeders = feeders.Count;
        feeders = Readable(feeders, line, siblings, earlier, w);
        if (feeders.Count > 0)
            result.Notes.Add($"KID's {feeders.Count:N0} other {asked.Type} line(s) of the layer were replayed first, in KID's order, so a keyword they give counts; the other {layerFeeders - feeders.Count:N0} give none it reads.");
        else if (layerFeeders > 0)
            result.Notes.Add($"None of KID's {layerFeeders:N0} other {asked.Type} line(s) of the layer gives a keyword it reads, so none was replayed.");
        if (line.ChanceText is not null)
            result.Notes.Add($"The line's chance is {line.ChanceText} as written; KID rolls it from the keyword's EditorID and the item's FormID, read here.");
        // Magic Effect lines run before every other type, so a magic item's costliest effect carries what they give.
        if (asked.Type != "Magic Effect" && (line.ReadsKeywords || feeders.Any(f => f.ReadsKeywords)))
            w.MgefKw = MagicEffects(layer, ctx, created, w);

        var getter = SpidGrammar.Getter(type.Record)!;
        string? edidGap = world.EditorIdGap(type.Record);
        foreach (var body in world.Winners(getter))
        {
            world.Ct.ThrowIfCancellationRequested();
            if (DeletedRecordRule.HasNoLiveBody(body)) continue;
            Verdict v;
            try
            {
                var item = Open(body, type.Record, edidGap, w);
                Replay(feeders, item, w);
                if (item.Own.Contains(line.KeywordId)) { result.AddHeld(body.FormKey); continue; }
                v = Verdict.Any(siblings, s => Holds(item, s)).Not();
                if (v.V != Reach.No) v = Verdict.And(v, Eval(line, item, w));
                if (v.V != Reach.No && earlier.Count > 0)
                {
                    var prior = Verdict.Any(earlier, f => Eval(f, item, w));
                    if (prior.V == Reach.Yes) { result.AddHeld(body.FormKey); continue; }
                    if (prior.V != Reach.No) v = Verdict.And(v, new Verdict(prior.V, $"an earlier line of the same keyword may tag it first ({prior.Why})"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                v = Verdict.Undecided($"its record could not be read ({ex.GetType().Name})");
            }
            result.Add(body.FormKey, v);
        }
        return result;
    }

    /// <summary>The feeders whose keyword the asked line or an earlier line of its form can read, directly or through another feeder it reads: a
    /// keyword no keyword term, partial string or group sibling of the line names changes none of its verdicts. A line
    /// reading a form list keeps every feeder, since a list may hold a keyword.</summary>
    static List<Line> Readable(List<Line> feeders, Line asked, List<string> siblings, List<Line> earlier, World w)
    {
        var ids = new HashSet<string>(siblings, StringComparer.Ordinal);
        var part = new List<string>();
        bool all = false;
        void Reads(Line l)
        {
            foreach (var r in l.Any.Concat(l.All.SelectMany(x => x)))
            {
                if (r.Keyword is { } k) ids.Add(k);
                else if (r.Part is { } p && !r.Model) part.Add(p);
                else if (r.Cls is { } c && typeof(IKeywordGetter).IsAssignableFrom(c)) ids.Add(Id(r.Form!.Value));
                else if (r.Cls is { } fc && typeof(IFormListGetter).IsAssignableFrom(fc)) all = true;
            }
        }
        Reads(asked);
        foreach (var l in earlier) Reads(l);
        var kept = new HashSet<Line>();
        for (bool grew = true; grew && !all;)
        {
            grew = false;
            foreach (var f in feeders)
            {
                if (kept.Contains(f)) continue;
                var eid = EidOf(f.KeywordId, w)?.ToLowerInvariant() ?? "";
                if (!ids.Contains(f.KeywordId) && !part.Any(p => eid.Contains(p, StringComparison.Ordinal))) continue;
                kept.Add(f); Reads(f); grew = true;
            }
        }
        return all ? feeders : feeders.Where(kept.Contains).ToList();
    }

    static bool IsFormId(string raw, KidLines.Context ctx)
        => RuntimeTargets.Read(raw, ctx.S).Kind is RuntimeTargets.TargetKind.Address or RuntimeTargets.TargetKind.RuntimeFormId;

    static Item Open(IMajorRecordGetter body, string record, string? edidGap, World w)
    {
        var it = new Item { Body = body, Record = record };
        if (body is Mutagen.Bethesda.Plugins.Aspects.IKeywordedGetter<IKeywordGetter> k && k.Keywords is { } kws) foreach (var x in kws) { it.Own.Add(Id(x.FormKey)); if (w.KwEid.TryGetValue(x.FormKey, out var e)) it.OwnEids.Add(e.ToLowerInvariant()); }
        it.Edid = edidGap is null ? body.EditorID?.ToLowerInvariant() : null;
        it.Name = (body is Mutagen.Bethesda.Plugins.Aspects.INamedGetter n ? n.Name : null)?.ToLowerInvariant() ?? "";
        it.Model = ModelPath(body)?.ToLowerInvariant() ?? "";
        return it;
    }

    static string? ModelPath(object body)
    {
        var model = body.GetType().GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)?.GetValue(body);
        var file = model?.GetType().GetProperty("File")?.GetValue(model);
        return file?.GetType().GetProperty("GivenPath")?.GetValue(file) as string ?? file?.ToString();
    }

    /// <summary>KID's per-item pass in layer order, repeated until nothing is added (docs/architecture/record-reach.md).</summary>
    static void Replay(List<Line> feeders, Item item, World w)
    {
        if (feeders.Count == 0) return;
        bool changed = true, first = true;
        for (int pass = 0; changed && pass < 16; pass++)
        {
            changed = false;
            foreach (var f in feeders)
            {
                if (!first && !f.ReadsKeywords) continue;
                if (item.Own.Contains(f.KeywordId)) continue;
                if (item.Added.TryGetValue(f.KeywordId, out var had) && had.V == Reach.Yes) continue;
                var v = Eval(f, item, w);
                if (v.V == Reach.No || had.V == v.V) continue;
                if (had.V != Reach.No && v.V != Reach.Yes) continue;
                item.Added[f.KeywordId] = v.V == Reach.Yes ? Verdict.Yes
                    : new Verdict(v.V, $"a line of the type that gives a keyword it reads ({v.Why})");
                changed = true;
            }
            first = false;
        }
    }

    static Verdict Holds(Item item, string id)
        => item.Own.Contains(id) ? Verdict.Yes : item.Added.TryGetValue(id, out var a) ? a : Verdict.No;

    // ---- one line over one item -------------------------------------------------------------------

    /// <summary>Chance, then traits, then filters (BaseFilters.cpp:247-262).</summary>
    static Verdict Eval(Line l, Item item, World w)
    {
        var v = Verdict.Yes;
        if (l.Chance < 1f)
        {
            if (w.W.RuntimeFormId(item.Body.FormKey) is not { } rt) v = Verdict.Undecided("its runtime FormID, which KID's chance reads, cannot be stated");
            else if (Roll(l.KeywordEid, rt) > l.Chance) return Verdict.No;
        }
        if (l.Traits is { } t) { v = Verdict.And(v, t(item.Body, w)); if (v.V == Reach.No) return v; }
        return Verdict.And(v, Pass(l, item, w));
    }

    /// <summary>KID's filter pass (BaseFilters.cpp:127-218): one '+' group whole, and the plain list's exact and partial terms.</summary>
    static Verdict Pass(Line l, Item item, World w)
    {
        if (l.All.Count == 0 && l.Any.Count == 0) return Verdict.Yes;
        var v = Verdict.Yes;
        if (l.All.Count > 0)
            v = Verdict.Any(l.All, grp => Verdict.All(grp, r => r.Not ? Match(r, item, w).Not() : Match(r, item, w)));
        if (v.V == Reach.No || l.Any.Count == 0) return v;
        v = Verdict.And(v, Verdict.All(l.Excluded, r => Match(r, item, w).Not()));
        if (l.Exact.Count > 0) v = Verdict.And(v, Verdict.Any(l.Exact, r => Match(r, item, w)));
        if (l.Partial.Count > 0) v = Verdict.And(v, Verdict.Any(l.Partial, r => Match(r, item, w)));
        return v;
    }

    static Verdict Match(Rule r, Item item, World w)
    {
        if (r.Part is { } p)
        {
            if (r.Model) return Verdict.Of(item.Model.Contains(p, StringComparison.Ordinal));
            if (item.Edid?.Contains(p, StringComparison.Ordinal) == true || item.Name.Contains(p, StringComparison.Ordinal)) return Verdict.Yes;
            foreach (var e in item.OwnEids) if (e.Contains(p, StringComparison.Ordinal)) return Verdict.Yes;
            return Verdict.Any(item.Added.Where(kv => EidOf(kv.Key, w)?.Contains(p, StringComparison.OrdinalIgnoreCase) == true), kv => kv.Value);
        }
        if (r.Exact is { } s)
        {
            if (r.Model) return Verdict.Of(item.Model == s);
            if (item.Edid == s || item.Name == s) return Verdict.Yes;
            return r.Named ? Verdict.Undecided("an actor-value or archetype name, which KID reads off the item or its costliest effect") : Verdict.No;
        }
        if (r.Plugin is { } plugin) return Verdict.Of(item.Body.FormKey.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (r.Keyword is { } kid) return Keyword(kid, item, w);
        return Form(r.Form!.Value, r.Cls, item, w, 0);
    }

    static string? EidOf(string id, World w)
        => id.StartsWith('+') ? id[1..] : w.KwEid.GetValueOrDefault(FormKey.Factory(id));

    /// <summary>A keyword filter (ItemData.cpp:50-75): the item holds it, else its costliest effect's magic effect does,
    /// for a magic item, a spell tome's spell, an enchanted armour or weapon.</summary>
    static Verdict Keyword(string id, Item item, World w)
    {
        var held = Holds(item, id);
        if (held.V == Reach.Yes) return held;
        var effects = CostliestOf(item, w);
        if (effects is null) return held;
        return Verdict.Or(held, Costliest(effects, m => w.MgefKw is { } mk && mk.TryGetValue(m, out var set)
            ? set.TryGetValue(id, out var x) ? x : Verdict.No : Verdict.No));
    }

    /// <summary>The magic effects whose costliest decides a keyword test: a magic item's own, a tome's spell's, an
    /// enchantment's; null when KID reads no costliest effect for this item.</summary>
    static FormKey[]? CostliestOf(Item item, World w)
    {
        var b = item.Body;
        if (b is ISpellGetter or IScrollGetter or IIngestibleGetter or IIngredientGetter or IObjectEffectGetter)
        {
            var own = EffectKeys(b);
            if (own.Length > 0) return own;
        }
        FormKey? via = b switch
        {
            IBookGetter book => book.Teaches is IBookSpellGetter s && !s.Spell.IsNull ? s.Spell.FormKey : null,
            IArmorGetter a => a.ObjectEffect is { IsNull: false } ae ? ae.FormKey : null,
            IWeaponGetter wp => wp.ObjectEffect is { IsNull: false } we ? we.FormKey : null,
            _ => null,
        };
        if (via is null) return null;
        ReadEffects(w);
        return w.Effects.TryGetValue(via.Value, out var fx) && fx.Length > 0 ? fx : null;
    }

    static FormKey[] EffectKeys(object body)
        => ReachFields.Links(body, "Effects[*].BaseEffect").ToArray();

    /// <summary>A test on the costliest effect: decided when every effect gives the same answer, else not decidable,
    /// since which effect costs most is the engine's computation.</summary>
    static Verdict Costliest(FormKey[] effects, Func<FormKey, Verdict> test)
    {
        var answers = effects.Distinct().Select(test).ToList();
        if (answers.All(a => a.V == answers[0].V)) return answers[0];
        return Verdict.Undecided("its costliest effect decides, which the engine computes");
    }

    /// <summary>A filter form of any other type (ItemData.cpp:43-233): the item itself, a location and its parents, a
    /// form list's members, else the item field the data names for the form's type and the item's.</summary>
    static Verdict Form(FormKey f, Type? cls, Item item, World w, int depth)
    {
        if (item.Body.FormKey == f) return Verdict.Yes;
        if (cls is null) return Verdict.No;
        if (typeof(IKeywordGetter).IsAssignableFrom(cls)) return Keyword(Id(f), item, w);
        if (typeof(ILocationGetter).IsAssignableFrom(cls))
        {
            if (item.Body is not ILocationGetter loc) return Verdict.No;
            ReadLocations(w);
            var at = loc.ParentLocation is { IsNull: false } p ? p.FormKey : (FormKey?)null;
            for (int i = 0; at is { } a && i < 64; i++) { if (a == f) return Verdict.Yes; at = w.LocParent.GetValueOrDefault(a); }
            return Verdict.No;
        }
        if (typeof(IFormListGetter).IsAssignableFrom(cls))
        {
            if (item.Body is IObjectEffectGetter ench && ench.WornRestrictions.FormKey == f) return Verdict.Yes;
            if (depth > 16) return Verdict.Undecided("a form list that nests too deep");
            if (!w.Lists.TryGetValue(f, out var members)) w.Lists[f] = members = w.W.ListItems(f);
            if (members is null) return Verdict.No;
            return Verdict.Any(members, m => Form(m, w.W.ClassOf(m), item, w, depth + 1));
        }
        var filterRec = w.G.FilterTypes.FirstOrDefault(n => SpidGrammar.IsOneOf(cls, new[] { n }));
        if (filterRec is null || !w.F.KidForms.TryGetValue((filterRec, item.Record), out var paths)) return Verdict.No;
        return Verdict.Of(paths.Any(path => ReachFields.Links(item.Body, path).Contains(f)));
    }

    // ---- traits -------------------------------------------------------------------------------

    static readonly Regex RangeRe = new(@"^[^-+.\d]*([-+]?\d*\.?\d+)(?:[^-+.\d]+([-+]?\d*\.?\d+))?.*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    static readonly Regex SkillRe = new(@"^[^-+.\d]*([-+]?\d*\.?\d+)[^-+.\d]+([-+]?\d*\.?\d+)(?:[^-+.\d]+([-+]?\d*\.?\d+))?.*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    static readonly Regex NumRe = new(@"[-+]?\d*\.?\d+", RegexOptions.CultureInvariant);

    static (double Min, double Max)? Range(string t)
    {
        var m = RangeRe.Match(t);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) return null;
        var hi = m.Groups[2].Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : lo;
        return (lo, hi);
    }

    static int? Single(string t)
        => NumRe.Match(t) is { Success: true } m && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;

    static bool In((double Min, double Max) r, double v) => v >= r.Min && v <= r.Max;

    // KID numbers an actor value as the game does; Mutagen's ActorValue and Skill enums run in the same order.
    static readonly ActorValue[] Avs = Enum.GetValues<ActorValue>();
    static readonly Skill[] Skills = Enum.GetValues<Skill>();
    static int Av(ActorValue av) => Array.IndexOf(Avs, av);
    static int Av(Skill? s) => s is { } k ? Array.IndexOf(Skills, k) + Av(ActorValue.OneHanded) : -1;

    /// <summary>The line's traits as KID 4.0.6 reads them per type (include/Data/Traits.h); a trait KID does not know is ignored, as KID ignores it.</summary>
    static Func<IMajorRecordGetter, World, Verdict>? Traits(string typeKey, string section)
    {
        var traits = section.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var tests = new List<Func<IMajorRecordGetter, World, Verdict>>();
        void Add(Func<IMajorRecordGetter, World, Verdict> f) => tests.Add(f);
        foreach (var t in traits)
        {
            switch (typeKey)
            {
                case "Armor":
                    if (t.Contains("AR(") && Range(t) is { } ar) Add((b, _) => Verdict.Of(In(ar, ((IArmorGetter)b).ArmorRating)));
                    else if (t.Contains("W(") && Range(t) is { } aw) Add((b, _) => Verdict.Of(In(aw, ((IArmorGetter)b).Weight)));
                    else if (t.Length > 0 && t.All(char.IsAsciiDigit) && int.TryParse(t, out var slot) && slot >= 30 && slot < 62)
                        Add((b, _) => Verdict.Of(((IArmorGetter)b).BodyTemplate is { } bt && ((ulong)bt.FirstPersonFlags & (1UL << (slot - 30))) != 0));
                    else if (t is "HEAVY" or "LIGHT" or "CLOTHING")
                    {
                        var want = t == "HEAVY" ? ArmorType.HeavyArmor : t == "LIGHT" ? ArmorType.LightArmor : ArmorType.Clothing;
                        Add((b, _) => ((IArmorGetter)b).BodyTemplate is { } bt ? Verdict.Of(bt.ArmorType == want) : Verdict.Undecided("an armour with no body template"));
                    }
                    else if (t is "E" or "-E") Add((b, _) => Verdict.Of(((IArmorGetter)b).ObjectEffect is { IsNull: false } == (t == "E")));
                    else if (t is "T" or "-T") Add((b, _) => Verdict.Of(((IArmorGetter)b).TemplateArmor is { IsNull: false } == (t == "T")));
                    break;
                case "Weapon":
                    if (t.Contains("W(") && Range(t) is { } ww) Add((b, _) => Verdict.Of(((IWeaponGetter)b).BasicStats is { } s && In(ww, s.Weight)));
                    else if (t.Contains("D(") && Range(t) is { } wd) Add((b, _) => Verdict.Of(((IWeaponGetter)b).BasicStats is { } s && In(wd, s.Damage)));
                    else if (WeaponTypes.IndexOf(t) is var at and >= 0)
                        Add((b, _) => Verdict.Of(((IWeaponGetter)b).Data is { } d && (int)d.AnimationType == at));
                    else if (t is "E" or "-E") Add((b, _) => Verdict.Of(((IWeaponGetter)b).ObjectEffect is { IsNull: false } == (t == "E")));
                    else if (t is "T" or "-T") Add((b, _) => Verdict.Of(((IWeaponGetter)b).Template is { IsNull: false } == (t == "T")));
                    break;
                case "Ammo":
                    if (t.Contains("D(") && Range(t) is { } md) Add((b, _) => Verdict.Of(In(md, ((IAmmunitionGetter)b).Damage)));
                    else if (t is "B" or "-B") Add((b, _) => Verdict.Of(!((IAmmunitionGetter)b).Flags.HasFlag(Ammunition.Flag.NonBolt) == (t == "B")));
                    break;
                case "Magic Effect":
                    if (t is "H" or "-H") Add((b, _) => Verdict.Of(((IMagicEffectGetter)b).Flags.HasFlag(MagicEffect.Flag.Hostile) == (t == "H")));
                    else if (t is "DISPEL" or "-DISPEL") Add((b, _) => Verdict.Of(((IMagicEffectGetter)b).Flags.HasFlag(MagicEffect.Flag.DispelWithKeywords) == (t == "DISPEL")));
                    else if (t.Contains("D(")) { var n = Single(t); if (n is { } dn) Add((b, _) => Verdict.Of((int)((IMagicEffectGetter)b).TargetType == dn)); }
                    else if (t.Contains("CT(")) { var n = Single(t); if (n is { } cn) Add((b, _) => Verdict.Of((int)((IMagicEffectGetter)b).CastType == cn)); }
                    else if (t.Contains("R(")) { var n = Single(t); if (n is { } rn) Add((b, _) => Verdict.Of(Av(((IMagicEffectGetter)b).ResistValue) == rn)); }
                    else if (t.Contains('(') && SkillRe.Match(t) is { Success: true } sm)
                    {
                        int sk = (int)double.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                        int lo = (int)double.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture);
                        int hi = sm.Groups[3].Success ? (int)double.Parse(sm.Groups[3].Value, CultureInfo.InvariantCulture) : int.MaxValue;
                        Add((b, _) => { var m = (IMagicEffectGetter)b; return Verdict.Of(Av(m.MagicSkill) == sk && m.MinimumSkillLevel >= lo && m.MinimumSkillLevel <= hi); });
                    }
                    break;
                case "Potion":
                    if (t is "P" or "-P") Add((b, _) => Verdict.Of(((IIngestibleGetter)b).Flags.HasFlag(Ingestible.Flag.Poison) == (t == "P")));
                    else if (t is "F" or "-F") Add((b, _) => Verdict.Of(((IIngestibleGetter)b).Flags.HasFlag(Ingestible.Flag.FoodItem) == (t == "F")));
                    break;
                case "Ingredient":
                    // KID reads the whole section as one trait here (IngredientTraits).
                    if (section is "F" or "-F") Add((b, _) => Verdict.Of(((IIngredientGetter)b).Flags.HasFlag(Ingredient.Flag.FoodItem) == (section == "F")));
                    break;
                case "Book":
                    if (t is "S" or "-S") Add((b, _) => Verdict.Of(((IBookGetter)b).Teaches is IBookSpellGetter == (t == "S")));
                    else if (t is "AV" or "-AV") Add((b, _) => Verdict.Of(((IBookGetter)b).Teaches is IBookSkillGetter == (t == "AV")));
                    else if (t.Length > 0 && t.All(char.IsAsciiDigit) && int.TryParse(t, out var bav))
                        Add((b, w) => BookSkill((IBookGetter)b, bav, w));
                    break;
                case "Soul Gem":
                    if (t is "BLACK" or "-BLACK") Add((b, _) => Verdict.Of(((ISoulGemGetter)b).MajorFlags.HasFlag(SoulGem.MajorFlag.CanHoldNpcSoul) == (t == "BLACK")));
                    else if (t.Contains("SOUL(")) { var n = Single(t); if (n is { } sn) Add((b, _) => Verdict.Of((int)((ISoulGemGetter)b).ContainedSoul == sn)); }
                    else { var n = Single(t); if (n is { } gn) Add((b, _) => Verdict.Of((int)((ISoulGemGetter)b).MaximumCapacity == gn)); }
                    break;
                case "Spell" or "Scroll" or "Enchantment":
                    if (t is "H" or "-H") Add((_, _) => Verdict.Undecided("trait H on a spell, hostile when an effect is, which is not settled"));
                    else if (t.Length > 0 && t.All(char.IsAsciiDigit) && int.TryParse(t, out var sav))
                        Add((b, w) => Costliest(EffectKeys(b), m => { ReadMgef(w); return Verdict.Of(w.MgefSkill.GetValueOrDefault(m, -1) == sav); }));
                    else if (t.Contains("ST(")) { var n = Single(t); if (n is { } st) Add((b, _) => Verdict.Of(SpellType(b) == st)); }
                    else if (t.Contains("D(")) { var n = Single(t); if (n is { } dl) Add((b, _) => Verdict.Of(Delivery(b) == dl)); }
                    else if (t.Contains("CT(")) { var n = Single(t); if (n is { } ct) Add((b, _) => Verdict.Of(CastingType(b) == ct)); }
                    break;
                case "Furniture":
                    if (t.Contains("BT(")) { var n = Single(t); if (n is { } bt) Add((b, _) => Verdict.Of(((IFurnitureGetter)b).WorkbenchData is { } d && (int)d.BenchType == bt)); }
                    else if (t.Contains("US(")) { var n = Single(t); if (n is { } us) Add((b, _) => Verdict.Of(Av(((IFurnitureGetter)b).WorkbenchData?.UsesSkill) == us)); }
                    else if (t.Contains("T(")) Add((_, _) => Verdict.Undecided("a furniture type, read off its markers"));
                    break;
            }
        }
        if (tests.Count == 0) return null;
        return (b, w) => Verdict.All(tests, f => f(b, w));
    }

    static readonly List<string> WeaponTypes = ["HandToHandMelee", "OneHandSword", "OneHandDagger", "OneHandAxe", "OneHandMace", "TwoHandSword", "TwoHandAxe", "Bow", "Staff", "Crossbow"];

    static int SpellType(IMajorRecordGetter b) => b switch
    {
        ISpellGetter s => (int)s.Type, IScrollGetter sc => (int)sc.Type, IObjectEffectGetter e => (int)e.EnchantType, _ => -1,
    };
    static int Delivery(IMajorRecordGetter b) => b switch
    {
        ISpellGetter s => (int)s.TargetType, IScrollGetter sc => (int)sc.TargetType, IObjectEffectGetter e => (int)e.TargetType, _ => -1,
    };
    static int CastingType(IMajorRecordGetter b) => b switch
    {
        ISpellGetter s => (int)s.CastType, IScrollGetter sc => (int)sc.CastType, IObjectEffectGetter e => (int)e.CastType, _ => -1,
    };

    /// <summary>KID's book actor value: it fails only when the book teaches another skill and a spell whose costliest effect is of another skill.</summary>
    static Verdict BookSkill(IBookGetter b, int av, World w)
    {
        int skill = b.Teaches is IBookSkillGetter s ? Av(s.Skill) : -1;
        if (skill == av) return Verdict.Yes;
        if (b.Teaches is not IBookSpellGetter sp || sp.Spell.IsNull) return Verdict.Yes;
        ReadEffects(w);
        if (!w.Effects.TryGetValue(sp.Spell.FormKey, out var fx) || fx.Length == 0) return Verdict.No;
        ReadMgef(w);
        return Costliest(fx, m => Verdict.Of(w.MgefSkill.GetValueOrDefault(m, -1) == av));
    }

    // ---- what an item reads beyond itself, read once per call -----------------------------------------

    static void ReadEffects(World w)
    {
        if (w.EffectsRead) return;
        w.EffectsRead = true;
        foreach (var t in new[] { typeof(IObjectEffectGetter), typeof(ISpellGetter) })
            foreach (var b in w.W.Winners(t))
                try { w.Effects[b.FormKey] = EffectKeys(b); } catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    static void ReadMgef(World w)
    {
        if (w.MgefRead) return;
        w.MgefRead = true;
        foreach (var b in w.W.Winners(typeof(IMagicEffectGetter)))
            try { w.MgefSkill[b.FormKey] = Av(((IMagicEffectGetter)b).MagicSkill); } catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    static void ReadLocations(World w)
    {
        if (w.LocRead) return;
        w.LocRead = true;
        foreach (var b in w.W.Winners(typeof(ILocationGetter)))
            try { w.LocParent[b.FormKey] = ((ILocationGetter)b).ParentLocation is { IsNull: false } p ? p.FormKey : null; }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    /// <summary>Every magic effect's keywords after KID's Magic Effect lines, which run before every other type.</summary>
    static Dictionary<FormKey, Dictionary<string, Verdict>> MagicEffects(IReadOnlyList<(KidLines.Line Line, KidLines.LineRead? Read)> layer,
                                                                         KidLines.Context ctx, HashSet<string> created, World w)
    {
        var lines = Feeders(layer, "Magic Effect", null, ctx, created, w);
        var map = new Dictionary<FormKey, Dictionary<string, Verdict>>();
        var gap = w.W.EditorIdGap("MagicEffect");
        foreach (var b in w.W.Winners(typeof(IMagicEffectGetter)))
        {
            w.W.Ct.ThrowIfCancellationRequested();
            try
            {
                var item = Open(b, "MagicEffect", gap, w);
                Replay(lines, item, w);
                var set = item.Own.ToDictionary(k => k, _ => Verdict.Yes, StringComparer.Ordinal);
                foreach (var (k, v) in item.Added) set.TryAdd(k, v);
                map[b.FormKey] = set;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return map;
    }

    // ---- compiling -----------------------------------------------------------------------------------

    /// <summary>The type's kept lines but the asked keyword's, compiled, in KID's line order per keyword.</summary>
    static List<Line> Feeders(IReadOnlyList<(KidLines.Line Line, KidLines.LineRead? Read)> layer, string type, string? except,
                              KidLines.Context ctx, HashSet<string> created, World w)
    {
        var lines = new List<Line>();
        foreach (var (l, r) in layer)
        {
            if (l.Skipped is not null || l.IsGroup || l.Type != type || r is not { Dropped: null }) continue;
            var c = Compile(l, ctx, created, w);
            if (c.KeywordId != except) lines.Add(c);
        }
        return lines;
    }

    /// <summary>The type's kept lines of the asked keyword before the asked line in the layer (every one when it is not in
    /// the layer), compiled.</summary>
    static List<Line> Earlier(IReadOnlyList<(KidLines.Line Line, KidLines.LineRead? Read)> layer, KidLines.Line asked, string keywordId,
                              KidLines.Context ctx, HashSet<string> created, World w)
    {
        var self = Parsed(asked, ctx.G);
        var lines = new List<Line>();
        foreach (var (l, r) in layer)
        {
            if (Parsed(l, ctx.G) == self) break;
            if (l.Skipped is not null || l.IsGroup || l.Type != asked.Type || r is not { Dropped: null }) continue;
            var c = Compile(l, ctx, created, w);
            if (c.KeywordId == keywordId) lines.Add(c);
        }
        return lines;
    }

    /// <summary>A line as KID parses it, in one case, so a copy spaced or cased otherwise is the same line.</summary>
    static string Parsed(KidLines.Line l, KidGrammar g)
    {
        var secs = l.Raw[(l.Raw.IndexOf(g.Assign) + 1)..].Split(g.Sections).Skip(3).Select(s => string.Concat(s.Where(c => !char.IsWhiteSpace(c))));
        return string.Join("|", new[] { l.IsGroup.ToString(), l.Type ?? "", l.Keyword.Trim() }
            .Concat(l.Tokens.Select(t => $"{t.Field}:{t.Not}:{t.Partial}:{t.Group}:{t.Raw.Trim()}")).Concat(secs)).ToLowerInvariant();
    }

    static Line Compile(KidLines.Line src, KidLines.Context ctx, HashSet<string> created, World w)
    {
        var g = ctx.G;
        var l = new Line();
        (l.KeywordId, l.KeywordEid) = KeywordOf(src.Keyword, ctx, w);
        var groups = new SortedDictionary<int, (List<Rule> Rules, bool Valid)>();
        foreach (var t in src.Tokens)
        {
            if (t.Field != KidLines.KidField.Filters) continue;
            var rule = RuleOf(t, ctx, created, w);
            if (t.Group >= 0)
            {
                if (!groups.TryGetValue(t.Group, out var gr)) groups[t.Group] = gr = (new(), true);
                if (rule is null) groups[t.Group] = (gr.Rules, false); else gr.Rules.Add(rule);
            }
            else if (rule is not null) { l.Any.Add(rule); (rule.Not ? l.Excluded : rule.Part is null ? l.Exact : l.Partial).Add(rule); }
        }
        // A '+' group with a part KID cannot use is dropped whole (BaseFilters.cpp:105-118).
        l.All = groups.Values.Where(x => x.Valid && x.Rules.Count > 0).Select(x => x.Rules).ToList();
        l.ReadsKeywords = l.Any.Concat(l.All.SelectMany(x => x)).Any(r => r.Keyword is not null || r.Part is not null && !r.Model
            || r.Cls is { } c && (typeof(IKeywordGetter).IsAssignableFrom(c) || typeof(IFormListGetter).IsAssignableFrom(c)));
        var secs = src.Raw[(src.Raw.IndexOf(g.Assign) + 1)..].Trim().Split(g.Sections);
        var type = g.Type(src.Type!)!;
        if (secs.Length > 3 && g.Set(secs[3]) && type.Traits) l.Traits = Traits(type.Key, secs[3]);
        if (secs.Length > 4 && g.Set(secs[4]) && NumRe.Match(secs[4].TrimStart()) is { Success: true } cm
            && float.TryParse(cm.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ch) && ch / 100.0f < 1f)
        { l.Chance = ch / 100.0f; l.ChanceText = secs[4].Trim(); }
        return l;
    }

    /// <summary>The EditorID of the keyword a line gives: a keyword record's, or the name KID creates.</summary>
    public static string KeywordName(KidLines.Line l, KidLines.Context ctx)
    {
        var read = RuntimeTargets.Read(l.Keyword, ctx.S);
        if (read.Kind is not (RuntimeTargets.TargetKind.Address or RuntimeTargets.TargetKind.RuntimeFormId)) return l.Keyword;
        return RuntimeTargets.Resolve(read, null, ctx.Resolver, ctx.S) is { } fk ? ctx.Resolver.EditorIdOf(fk) ?? l.Keyword : l.Keyword;
    }

    /// <summary>The keyword section's keyword as KID holds it, and the EditorID its chance seed reads.</summary>
    static (string Id, string Eid) KeywordOf(string raw, KidLines.Context ctx, World w)
    {
        var read = RuntimeTargets.Read(raw, ctx.S);
        if (read.Kind is RuntimeTargets.TargetKind.Address or RuntimeTargets.TargetKind.RuntimeFormId)
        {
            var fk = RuntimeTargets.Resolve(read, null, ctx.Resolver, ctx.S);
            return fk is { } k ? (Id(k), w.KwEid.GetValueOrDefault(k) ?? "") : (Created(raw), raw);
        }
        return ctx.Resolver.ResolveEditorId(raw, ctx.G.KeywordRecord) is { } hit ? (Id(hit), w.KwEid.GetValueOrDefault(hit) ?? raw) : (Created(raw), raw);
    }

    /// <summary>One filter term as KID resolves it (FormData.cpp:85-148), null when KID cannot use it.</summary>
    static Rule? RuleOf(KidLines.Token t, KidLines.Context ctx, HashSet<string> created, World w)
    {
        var g = ctx.G;
        bool model = t.Raw.EndsWith(g.ModelEnding, StringComparison.OrdinalIgnoreCase);
        if (t.Partial)
            return new Rule(t.Not, null, null, null, null, null, model, false, model ? SanitizePath(t.Raw) : t.Raw.ToLowerInvariant());
        if (model) return new Rule(t.Not, null, null, null, null, SanitizePath(t.Raw), true, false, null);
        var r = DistributorLines.Resolve(t.Raw, ctx.Resolver, ctx.S, "KID");
        if (r.Form is { } fk)
        {
            var cls = ctx.ClassOf(fk);
            return SpidGrammar.IsOneOf(cls, g.FilterTypes) ? new Rule(t.Not, fk, cls, null, null, null, false, false, null) : null;
        }
        if (r.Plugin is { } p) return r.PluginPresent ? new Rule(t.Not, null, null, null, p, null, false, false, null) : null;
        if (!r.ByEditorId) return null;
        if (created.Contains(t.Raw)) return new Rule(t.Not, null, null, Created(t.Raw), null, null, false, false, null);
        bool named = w.F.KidActorValues.Contains(t.Raw) || w.F.KidArchetypes.Contains(t.Raw);
        return new Rule(t.Not, null, null, null, null, t.Raw.ToLowerInvariant(), false, named, null);
    }

    static readonly Regex Slashes = new(@"/+|\\+", RegexOptions.CultureInvariant);
    static readonly Regex Leading = new(@"^\\+", RegexOptions.CultureInvariant);
    static readonly Regex Meshes = new(@".*?[^\s]meshes\\|^meshes\\", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>KID's <c>SanitizePath</c> (include/RE.h): lower case, one backslash, no leading one, nothing up to 'meshes\'.</summary>
    static string SanitizePath(string s)
    {
        s = s.ToLowerInvariant();
        s = Slashes.Replace(s, "\\");
        s = Leading.Replace(s, "");
        return Meshes.Replace(s, "", 1);
    }

    /// <summary>The ExclusiveGroup siblings of a keyword: every other member of a group that holds it.</summary>
    static List<string> Siblings(IReadOnlyList<(KidLines.Line Line, KidLines.LineRead? Read)> layer, string id, KidLines.Context ctx, HashSet<string> created)
    {
        var sibs = new List<string>();
        foreach (var (l, r) in layer)
        {
            if (!l.IsGroup || l.Skipped is not null || r is not { Dropped: null }) continue;
            var members = l.Tokens.Where(t => !t.Not).Select(t =>
            {
                var res = DistributorLines.Resolve(t.Raw, ctx.Resolver, ctx.S, "KID");
                return res.Form is { } fk ? Id(fk) : res.ByEditorId && created.Contains(t.Raw) ? Created(t.Raw) : null;
            }).OfType<string>().ToList();
            if (members.Contains(id)) sibs.AddRange(members.Where(m => m != id));
        }
        return sibs.Distinct().ToList();
    }

    // ---- KID's chance roll (BaseFilters.cpp:221-237; CLibUtil 789abd2 hash.hpp, rng.hpp, XoshiroCpp.hpp) ---------

    /// <summary>The number KID draws for a keyword on an item: Xoshiro256** seeded through SplitMix64 with
    /// szudzik_pair(fnv1a_32(keyword EditorID), item FormID), its first output as a double in [0, 1).</summary>
    public static double Roll(string keywordEid, uint formId)
    {
        uint h = 0x811c9dc5;
        // MSVC's char is signed: a byte over 0x7F widens with its sign before the xor.
        foreach (var by in System.Text.Encoding.UTF8.GetBytes(keywordEid))
            h = unchecked((uint)((h ^ (uint)(sbyte)by) * 0x01000193UL));
        // szudzik_pair computes in 32 bits, then widens.
        ulong seed = h >= formId ? unchecked(h * h + h + formId) : unchecked(formId * formId + h);
        ulong sm = seed;
        ulong Next()
        {
            ulong z = sm = unchecked(sm + 0x9e3779b97f4a7c15UL);
            z = unchecked((z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94d049bb133111ebUL);
            return z ^ (z >> 31);
        }
        Next();
        ulong s1 = Next();   // Xoshiro256**'s first output reads only the second state word
        ulong result = unchecked(RotL(s1 * 5, 7) * 9);
        return (result >> 11) * (1.0 / (1UL << 53));
    }

    static ulong RotL(ulong x, int s) => (x << s) | (x >> (64 - s));
}

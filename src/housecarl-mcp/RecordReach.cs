using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

// The reach term on records where=: each SPID or KID line read with its layer in one replay context over the scan's
// own view, and judged over every record of its type by the format's own rules. Read-only; nothing is kept between calls.
internal sealed partial class RecordReads
{
    /// <summary>What the reach terms select: each term's answer, the record types they read, and the files that could
    /// not be read; or the refusal naming the line a format cannot read.</summary>
    internal sealed record ReachSelection(IReadOnlyList<ReachResult> Results, IReadOnlyList<string> Types, string Unread, string? Refusal)
    {
        /// <summary>The records no term says no to, in the first term's order: the yes records and the open ones.</summary>
        public IReadOnlyList<FormKey> Candidates
            => Results.Count == 0 ? [] : Results[0].Kept.Keys.Where(fk => Results.All(r => r.Of(fk).V != HousecarlCore.Reach.No)).ToList();

        /// <summary>Whether every term says yes to the record, so it is a row.</summary>
        public bool IsYes(FormKey fk) => Results.All(r => r.Yes.Contains(fk));

        /// <summary>Count the records the call's other terms keep into each term's scoped tally.</summary>
        public void Scope() { foreach (var r in Results) r.Scoped = new(); }

        /// <summary>A record the scan kept, counted into each term's scoped tally when the call is scoped.</summary>
        public void Tally(FormKey fk) { foreach (var r in Results) r.Scoped?.Add(fk, r.Of(fk)); }

        /// <summary>One note per term, then the unread files.</summary>
        public string Note() => string.Join(" ", Results.Select(r => r.Sentence())) + Unread;
    }

    /// <summary>Judge every reach term over <paramref name="view"/>, the layer read once per format in one replay context.</summary>
    internal ReachSelection Reach(IReadOnlyList<ReachTerm> terms, LoadOrderResolver.IndexView view, CancellationToken ct)
    {
        var captured = _host.CaptureAssets();
        using var session = _host.Resolver.OpenSession();
        var replay = _host.OpenSkyPatcherReplay(captured, view, session, out _, null, null)!;   // no draft, so never refused
        var world = new World(view, replay, ct);
        var fields = ReachFields.Load();
        var results = new List<ReachResult>();
        var unread = new List<string>();   // a file the format reads that could not be read here
        var spid = terms.Where(t => t.Format == "spid").ToList();
        var kid = terms.Where(t => t.Format == "kid").ToList();
        if (spid.Count > 0)
        {
            var format = RuntimeFormats.Spid;
            var g = format.Lines!;
            var asked = new List<SpidLines.Line>();
            foreach (var t in spid)
            {
                var parsed = SpidLines.Parse(t.Line, g);
                if (parsed.Count != 1) return Refuse(t, "is no SPID line (expected '<Type> = <form>|<strings>|<forms>|<level>|<traits>|<count>|<chance>')");
                var l = parsed[0];
                if (l.Skipped is { } why) return Refuse(t, $"SPID skips it: it {why}");
                if (l.Kind is SpidLineKind.ExclusiveGroup or SpidLineKind.Linked)
                    return Refuse(t, $"is {(l.Kind == SpidLineKind.Linked ? "a Linked line, which goes where its parent line goes; ask for the parent line" : "an ExclusiveGroup line, which hands nothing out")}");
                asked.Add(l);
            }
            var layer = Layer(format, replay, unread, (text, _) => SpidLines.Parse(text, g), ct);
            var raceTags = RaceTags(replay, view, world, fields, unread, ct);
            var ctx = new SpidLines.Context(replay.Forms, view.WinnerClassOf, format);
            // One read of every line, the asked ones last, so the call's EditorID table walks every type once.
            var reads = SpidLines.ReadAll(layer.Concat(asked).ToList(), ctx);
            var pairs = layer.Select((l, i) => (l, reads[i])).ToList();
            for (int i = 0; i < spid.Count; i++)
                results.Add(SpidReach.Evaluate(spid[i], asked[i], reads[layer.Count + i]!, pairs, ctx, world, fields, raceTags));
        }
        if (kid.Count > 0)
        {
            var format = RuntimeFormats.Kid;
            var asked = new List<KidLines.Line>();
            foreach (var t in kid)
            {
                var parsed = KidLines.Parse(t.Line, format);
                if (parsed.Count != 1) return Refuse(t, "is no KID line (expected '<keyword> = <keyword>|<type>|<filters>|<traits>|<chance>')");
                var l = parsed[0];
                if (l.Skipped is { } why) return Refuse(t, $"KID skips it: it {why}");
                if (l.IsGroup) return Refuse(t, "is an ExclusiveGroup line, which hands nothing out");
                asked.Add(l);
            }
            var layer = Layer(format, replay, unread, (text, _) => KidLines.Parse(text, format), ct);
            var ctx = new KidLines.Context(replay.Forms, view.WinnerClassOf, format);
            var reads = KidLines.ReadAll(layer.Concat(asked).ToList(), ctx);
            var pairs = layer.Select((l, i) => (l, reads[i])).ToList();
            for (int i = 0; i < kid.Count; i++)
                results.Add(KidReach.Evaluate(kid[i], asked[i], reads[layer.Count + i]!, pairs, ctx, world, fields));
        }
        // Several terms are ANDed like every where= term: the rows are the records every one says yes to.
        var ordered = terms.Select(t => results.First(r => ReferenceEquals(r.Term, t))).ToList();
        return new ReachSelection(ordered, ordered.Select(r => r.RecordType).Distinct().ToList(),
                                  unread.Count == 0 ? "" : $" {unread.Distinct().Count()} file(s) could not be read, so their lines were not replayed: {string.Join("; ", unread.Distinct())}.", null);

        static ReachSelection Refuse(ReachTerm t, string why) =>
            new(Array.Empty<ReachResult>(), Array.Empty<string>(), "", $"where= term '{t.Text}': the line {why}.");
    }

    /// <summary>The keywords KID's Race lines may give each race, judged by the KID reach over the races: SPID reads a
    /// race's keywords after KID has tagged it, and the SPID reach does not replay KID, so these stay open.</summary>
    static Dictionary<FormKey, List<string>> RaceTags(AssetLayers.SkyPatcherReplay replay, LoadOrderResolver.IndexView view, IReachWorld world,
                                                      ReachFields fields, List<string> unread, CancellationToken ct)
    {
        var tags = new Dictionary<FormKey, List<string>>();
        var format = RuntimeFormats.Kid;
        var race = Layer(format, replay, unread, (text, _) => KidLines.Parse(text, format), ct)
            .Where(l => l is { Skipped: null, IsGroup: false, Type: "Race" }).ToList();
        if (race.Count == 0) return tags;
        var ctx = new KidLines.Context(replay.Forms, view.WinnerClassOf, format);
        var reads = KidLines.ReadAll(race, ctx);
        var pairs = race.Select((l, i) => (l, reads[i])).ToList();
        foreach (var (l, r) in pairs)
        {
            if (r is not { Dropped: null }) continue;
            var answer = KidReach.Evaluate(new ReachTerm(l.Raw, "kid", l.Raw), l, r, pairs, ctx, world, fields);
            var name = KidReach.KeywordName(l, ctx);
            foreach (var fk in answer.Kept.Keys) { if (!tags.TryGetValue(fk, out var list)) tags[fk] = list = new(); list.Add(name); }
        }
        return tags;
    }

    /// <summary>Every line of the format's files as it finds them, in file order; a file it does not apply gives none, and one it cannot read is named in <paramref name="unread"/>.</summary>
    static List<TLine> Layer<TLine>(DistributorFormat format, AssetLayers.SkyPatcherReplay replay, List<string> unread, Func<string, string, IReadOnlyList<TLine>> parse, CancellationToken ct)
    {
        var lines = new List<TLine>();
        foreach (var (f, read, error) in AssetLayers.DistributorFiles(format, replay, parse, ct))
        {
            if (error is not null) unread.Add($"{format.Key} file '{f.RelPath}' ({error})");
            lines.AddRange(read);
        }
        return lines;
    }

    /// <summary>The load order as the evaluators read it: the scan's view, and the replay context's names and lists.</summary>
    sealed class World(LoadOrderResolver.IndexView view, AssetLayers.SkyPatcherReplay replay, CancellationToken ct) : IReachWorld
    {
        public CancellationToken Ct => ct;

        public IEnumerable<IMajorRecordGetter> Winners(Type getter)
            => view.WinnerRecordsOfType(new[] { getter }).Select(x => x.body);

        public Type? ClassOf(FormKey fk) => view.WinnerClassOf(fk);

        public IReadOnlyList<FormKey>? ListItems(FormKey list) => replay.ListItemsOf(list);

        public string? EditorIdGap(string mutagenType) => replay.Forms.EditorIdGap(mutagenType);

        public uint? RuntimeFormId(FormKey fk)
            => HousecarlCore.RuntimeFormId.TryParse(view.RuntimeAddressOf(fk).FormId, out var v) ? v : null;
    }
}

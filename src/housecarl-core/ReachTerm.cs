using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>One <c>where=</c> reach term: <c>spid reaches &lt;line&gt;</c> or <c>kid reaches &lt;line&gt;</c>, the rest of
/// the string after the operator taken whole as the line (contracts in docs/architecture/record-reach.md).</summary>
public sealed record ReachTerm(string Text, string Format, string Line)
{
    /// <summary>The operator word, between the format and the line.</summary>
    public const string Operator = "reaches";

    /// <summary>Split the where= list into its ordinary terms and its reach terms, each kept in written order. A term is a
    /// reach term when it starts with <c>spid</c> or <c>kid</c> (any case), then whitespace, then <c>reaches</c>, then
    /// whitespace; anything else is left to the field-predicate grammar untouched.</summary>
    public static (IReadOnlyList<string> Others, IReadOnlyList<ReachTerm> Terms) Split(IReadOnlyList<string>? where)
    {
        var rest = new List<string>();
        var terms = new List<ReachTerm>();
        foreach (var raw in where ?? Array.Empty<string>())
        {
            if (Read(raw) is { } t) terms.Add(t);
            else rest.Add(raw);
        }
        return (rest, terms);
    }

    /// <summary>Whether any term of the list is a reach term.</summary>
    public static bool AnyIn(IReadOnlyList<string>? where) => where?.Any(w => Read(w) is not null) ?? false;

    static ReachTerm? Read(string? raw)
    {
        var text = (raw ?? "").Trim();
        int sp = IndexOfSpace(text, 0);
        if (sp < 0) return null;
        var format = text[..sp];
        if (!format.Equals("spid", StringComparison.OrdinalIgnoreCase) && !format.Equals("kid", StringComparison.OrdinalIgnoreCase)) return null;
        int at = SkipSpace(text, sp);
        if (!text.AsSpan(at).StartsWith(Operator, StringComparison.OrdinalIgnoreCase)) return null;
        int after = at + Operator.Length;
        if (after < text.Length && !char.IsWhiteSpace(text[after])) return null;
        return new ReachTerm(text, format.ToLowerInvariant(), text[SkipSpace(text, after)..]);
    }

    static int IndexOfSpace(string s, int from)
    {
        for (int i = from; i < s.Length; i++) if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    static int SkipSpace(string s, int from)
    {
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;
        return from;
    }
}

/// <summary>A reach verdict for one record under one line: the files say no, the files say yes, the files cannot say
/// (not decidable), or only the running game decides.</summary>
public enum Reach { No, Yes, NotDecidable, Game }

/// <summary>A verdict and the first reason it is not a plain yes or no.</summary>
public readonly record struct Verdict(Reach V, string? Why = null)
{
    public static readonly Verdict Yes = new(Reach.Yes);
    public static readonly Verdict No = new(Reach.No);
    public static Verdict Undecided(string why) => new(Reach.NotDecidable, why);
    public static Verdict Game(string why) => new(Reach.Game, why);
    public static Verdict Of(bool b) => b ? Yes : No;

    // The running game outranks not decidable: a line takes its worst bucket.
    static int Rank(Reach r) => r switch { Reach.Yes => 0, Reach.NotDecidable => 1, Reach.Game => 2, _ => 3 };

    /// <summary>Both must hold: no beats every other answer, then the running game, then not decidable.</summary>
    public static Verdict And(Verdict a, Verdict b) => Rank(b.V) > Rank(a.V) ? b : a;

    /// <summary>Either holds: yes beats every other answer, then the running game, then not decidable, then no.</summary>
    public static Verdict Or(Verdict a, Verdict b)
    {
        if (a.V == Reach.Yes) return a;
        if (b.V == Reach.Yes) return b;
        if (a.V == Reach.No) return b;
        if (b.V == Reach.No) return a;
        return Rank(b.V) > Rank(a.V) ? b : a;
    }

    /// <summary>The opposite answer; an open answer stays open.</summary>
    public Verdict Not() => V switch { Reach.Yes => No, Reach.No => Yes, _ => this };

    /// <summary>Any of the items holds; an empty list is no.</summary>
    public static Verdict Any<T>(IEnumerable<T> xs, Func<T, Verdict> f)
    {
        var r = No;
        foreach (var x in xs) { r = Or(r, f(x)); if (r.V == Reach.Yes) break; }
        return r;
    }

    /// <summary>Every item holds; an empty list is yes.</summary>
    public static Verdict All<T>(IEnumerable<T> xs, Func<T, Verdict> f)
    {
        var r = Yes;
        foreach (var x in xs) { r = And(r, f(x)); if (r.V == Reach.No) break; }
        return r;
    }
}

/// <summary>What a reach evaluator needs of the load order, read through the call's own view and replay context.</summary>
public interface IReachWorld
{
    /// <summary>Every winning record of a getter type, streamed in load order.</summary>
    IEnumerable<IMajorRecordGetter> Winners(Type getter);

    /// <summary>The record class a key's winner is, null when the order holds none.</summary>
    Type? ClassOf(FormKey fk);

    /// <summary>A form list's members off its winner; null when it does not resolve.</summary>
    IReadOnlyList<FormKey>? ListItems(FormKey list);

    /// <summary>Why the game cannot see EditorIDs of this record type (po3 Tweaks missing), null when it can.</summary>
    string? EditorIdGap(string mutagenType);

    /// <summary>The FormID a record has in the running game, null when it cannot be stated.</summary>
    uint? RuntimeFormId(FormKey fk);

    CancellationToken Ct { get; }
}

/// <summary>One reach term's answer: the records the files say the line reaches, the records it may reach counted apart
/// with their reasons, and the sentences the answer owes.</summary>
public sealed class ReachResult
{
    public ReachResult(ReachTerm term, string recordType) { Term = term; RecordType = recordType; }

    public ReachTerm Term { get; }
    /// <summary>The Mutagen record type the line's universe is.</summary>
    public string RecordType { get; }
    /// <summary>Records judged.</summary>
    public int Universe { get; set; }
    /// <summary>Records the files say the line reaches.</summary>
    public HashSet<FormKey> Yes { get; } = new();
    /// <summary>Per open bucket: the count, the count by reason, and the first records.</summary>
    public Dictionary<Reach, Bucket> Open { get; } = new() { [Reach.NotDecidable] = new(), [Reach.Game] = new() };
    /// <summary>Records the line's filters select that already hold what it hands out, which the format skips.</summary>
    public int AlreadyHeld { get; set; }
    /// <summary>Sentences the answer owes, in order.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>The format drops the line, so nothing was judged.</summary>
    public bool Dropped { get; set; }

    /// <summary>Record one judged record.</summary>
    public void Add(FormKey fk, Verdict v)
    {
        Universe++;
        if (v.V == Reach.Yes) Yes.Add(fk);
        else if (v.V != Reach.No) Open[v.V].Add(fk, v.Why ?? "unstated");
    }

    /// <summary>One open bucket.</summary>
    public sealed class Bucket
    {
        public int Count { get; private set; }
        public Dictionary<string, int> ByReason { get; } = new(StringComparer.Ordinal);
        public List<FormKey> First { get; } = new();
        public void Add(FormKey fk, string why)
        {
            Count++;
            ByReason[why] = ByReason.GetValueOrDefault(why) + 1;
            if (First.Count < 3) First.Add(fk);
        }
    }

    /// <summary>The answer as one note: the counts by bucket, what the rows are, then the owed sentences.</summary>
    public string Sentence()
    {
        string Reasons(Bucket b) => b.Count == 0 ? "" :
            " (" + string.Join("; ", b.ByReason.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(4).Select(kv => $"{kv.Key}: {kv.Value:N0}")) + (b.ByReason.Count > 4 ? "; …" : "") +
            $"; e.g. {string.Join(", ", b.First.Select(FormIdToken.Of))})";
        var head = $"reach: {Term.Format} line '{Term.Line}'";
        if (Dropped) return head + ": " + string.Join(" ", Notes);
        var nd = Open[Reach.NotDecidable];
        var game = Open[Reach.Game];
        var s = $"{head} over {Universe:N0} {RecordName(RecordType)} record(s): " +
                $"yes {Yes.Count:N0}, not decidable {nd.Count:N0}{Reasons(nd)}, needs the running game {game.Count:N0}{Reasons(game)}. " +
                "The rows are the yes records only; the other two are counted apart and never an exact set.";
        if (AlreadyHeld > 0) s += $" {AlreadyHeld:N0} already hold the keyword, which KID skips, so the line does not reach them.";
        return Notes.Count == 0 ? s : s + " " + string.Join(" ", Notes);
    }

    static string RecordName(string t) => t == "Npc" ? "NPC" : t;
}

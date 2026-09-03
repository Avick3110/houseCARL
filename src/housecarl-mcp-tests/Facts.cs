using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The fact lane: an engine fact is asserted on a structural value reached by NAVIGATING to the record or
/// plugin it is about, and the navigation is the key.
///
/// <para><b>Why this exists</b> (issue #492). An assertion over a fragment of rendered prose stays green when
/// the thing it names is broken: the span cannot occur by fixture construction, so a negative is vacuous;
/// only a prefix is pinned, so a tail sabotage survives; a second render branch or a second RECORD in the
/// fixture emits the same line, so the arm is satisfied by something it is not about. None of those can
/// happen to a navigation — there is no span to prefix-pin, a second record cannot answer because you walked
/// past every other record to get here, and an absent subject fails loudly instead of reading as "not
/// there".</para>
///
/// <para><b>Every member here is loud.</b> Zero matches and two matches both throw, naming the key and what
/// was actually found; an absent wire member throws rather than reading as <c>0</c>, <c>""</c> or
/// <c>false</c>. That is the <see cref="HarnessPaths"/> rule — a helper that silently falls back is how a
/// test passes while measuring the wrong thing — and it is the whole reason a navigation is safer than a
/// span: the loudness is what makes "the subject was not there" a failure instead of a vacuous pass.</para>
///
/// <para><b>The one member that touches text</b> is <see cref="States"/>, and it asserts a catalogue
/// constant's IDENTITY, never a wording a test spelled out. A constant reference is whole-line by
/// construction, and the constant's WORDING is proven once at its home in ReadSentenceWordingTests — so this
/// arm is allowed to be exactly this thin. <see cref="SoleSubject"/> is what makes it safe on a text
/// response: it asserts the response describes exactly ONE record and that it is the one you meant, so a
/// rival producer of the same line cannot exist in that response.</para>
/// </summary>
static class Facts
{
    /// <summary>The wire spelling of a FormKey — <c>XXXXXX:Plugin.esp</c>, the same string the product's
    /// <c>formid</c> members carry.</summary>
    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    // ---- navigation ------------------------------------------------------------------------------------

    /// <summary>
    /// The ONE node in <paramref name="doc"/> that is about <paramref name="key"/> — an object carrying a
    /// <c>formid</c> member with that FormKey's wire spelling, found anywhere in the document.
    /// </summary>
    /// <remarks>
    /// Searched over the whole document rather than down a known path on purpose: the record node's ADDRESS
    /// differs per lane (a <c>records</c> array, a family section, an artifact row, a batch entry) and a
    /// helper that knew each path would be a second copy of the wire shape. What identifies a record node is
    /// its own <c>formid</c>, in every lane, so that is what is navigated by.
    /// </remarks>
    public static JsonElement Record(JsonDocument doc, FormKey key) => Record(doc.RootElement, key);

    /// <inheritdoc cref="Record(JsonDocument, FormKey)"/>
    public static JsonElement Record(JsonElement root, FormKey key)
    {
        var want = Fid(key);
        var hits = NodesKeyedBy(root, "formid", want);

        Assert.True(hits.Count == 1,
            hits.Count == 0
                ? $"No node in this response is about {want}: nothing carries that 'formid'. " +
                  $"Present instead: {Sample(IdentityValues(root, "formid"))}. A fact keyed by a record that " +
                  "is not in the response is the vacuous-negative generator this navigation exists to stop — " +
                  "either the fixture did not produce the record, or the lane did not render it."
                : $"{hits.Count} nodes in this response carry formid={want}, so a fact asserted here would " +
                  "not say which one it is about. A record renders once per response; if this lane really " +
                  "carries the same record twice, navigate to the section first and pass that element.");

        return hits[0];
    }

    /// <summary>The same, for a plugin-keyed node — an object carrying a <c>plugin</c> member with that
    /// name.</summary>
    public static JsonElement Plugin(JsonDocument doc, string name) => Plugin(doc.RootElement, name);

    /// <inheritdoc cref="Plugin(JsonDocument, string)"/>
    public static JsonElement Plugin(JsonElement root, string name)
    {
        var hits = NodesKeyedBy(root, "plugin", name);

        Assert.True(hits.Count == 1,
            hits.Count == 0
                ? $"No node in this response is about the plugin '{name}': nothing carries that 'plugin'. " +
                  $"Present instead: {Sample(IdentityValues(root, "plugin"))}."
                : $"{hits.Count} nodes in this response carry plugin='{name}', so a fact asserted here would " +
                  "not say which one it is about. Navigate to the section first and pass that element.");

        return hits[0];
    }

    /// <summary>
    /// The ONE field node inside <paramref name="rec"/> whose <c>path</c> is <paramref name="path"/>.
    /// </summary>
    public static JsonElement Field(JsonElement rec, string path)
    {
        var hits = NodesKeyedBy(rec, "path", path);

        Assert.True(hits.Count == 1,
            hits.Count == 0
                ? $"This record renders no field at path '{path}'. Rendered: {Sample(IdentityValues(rec, "path"))}. " +
                  "A field the lane did not emit cannot carry the fact you are asserting; if the point IS that " +
                  "it was not emitted, assert that over the rendered path set rather than over prose."
                : $"{hits.Count} field nodes inside this record carry path='{path}'.");

        return hits[0];
    }

    // ---- typed reads of one wire member ----------------------------------------------------------------

    /// <summary>A whole-number wire member. An absent member throws; it never reads as <c>0</c>.</summary>
    public static int Number(JsonElement el, string member)
    {
        var v = Member(el, member);
        Assert.True(v.ValueKind == JsonValueKind.Number,
            $"Wire member '{member}' is {v.ValueKind}, not a number — its raw text is {v.GetRawText()}.");
        Assert.True(v.TryGetInt32(out var n),
            $"Wire member '{member}' is a number this read cannot take as an Int32: {v.GetRawText()}.");
        return n;
    }

    /// <summary>A string wire member. An absent member throws; it never reads as <c>""</c>.</summary>
    public static string Text(JsonElement el, string member)
    {
        var v = Member(el, member);
        Assert.True(v.ValueKind == JsonValueKind.String,
            $"Wire member '{member}' is {v.ValueKind}, not a string — its raw text is {v.GetRawText()}.");
        return v.GetString()!;
    }

    /// <summary>A boolean wire member. An absent member throws; it never reads as <c>false</c>.</summary>
    public static bool Flag(JsonElement el, string member)
    {
        var v = Member(el, member);
        Assert.True(v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"Wire member '{member}' is {v.ValueKind}, not a boolean — its raw text is {v.GetRawText()}.");
        return v.GetBoolean();
    }

    /// <summary>The member itself, present or loudly absent — what makes the three typed reads above loud.</summary>
    public static JsonElement Member(JsonElement el, string member)
    {
        Assert.True(el.ValueKind == JsonValueKind.Object,
            $"Cannot read member '{member}': this node is {el.ValueKind}, not an object.");

        Assert.True(el.TryGetProperty(member, out var v),
            $"This node carries no wire member '{member}'. It carries: " +
            $"{Sample(el.EnumerateObject().Select(p => p.Name).ToList())}. An absent member read as a default " +
            "is the silent wrong answer this helper exists to refuse — if the claim is that the member is " +
            "ABSENT, assert that over the member set rather than over its value.");

        return v;
    }

    // ---- the text lane ---------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <paramref name="response"/> describes exactly ONE record and that it is
    /// <paramref name="key"/>, then hands the text back.
    /// </summary>
    /// <remarks>
    /// This is the answer to "two records in this fixture render the same line". A response with one subject
    /// cannot have a rival producer, by construction — so an arm over that text is about the record it says
    /// it is about, without anchoring a longer and longer span to prove it. It asserts an identity and
    /// returns the WHOLE response; it never slices. A reshape that starts slicing inside the returned text is
    /// the signal to take the fact to the json lane instead, not to grow a slicer here.
    /// </remarks>
    public static string SoleSubject(string response, FormKey key)
    {
        var want = Fid(key);
        var seen = FormIdTokens(response);

        Assert.True(seen.Count > 0,
            $"This response names no FormID at all, so it cannot be shown to be about {want}. " +
            "SoleSubject is for a response that renders records; a response that renders none has no subject " +
            "to be sole.");

        Assert.True(seen.Count == 1 && string.Equals(seen[0], want, StringComparison.OrdinalIgnoreCase),
            $"This response is not solely about {want}: it names {Sample(seen)}. A text arm over a response " +
            "with more than one subject can be satisfied by a line another record produced — narrow the call " +
            "(one formid, one scope) so the response has exactly one subject, or assert the fact on the json " +
            "document keyed by the record instead.");

        return response;
    }

    /// <summary>
    /// Reachability, by IDENTITY: <paramref name="text"/> states the catalogue sentence
    /// <paramref name="catalogueConstant"/>.
    /// </summary>
    /// <remarks>
    /// <para>Pass the CONSTANT, never a spelling of it — that is the whole point. A constant reference is
    /// whole-line by construction, so the prefix-pinning failure cannot occur, and the sentence's WORDING is
    /// proven once at its home rather than re-proven here.</para>
    /// <para>A format TEMPLATE is handled rather than refused: the constant is split at its <c>{N}</c> holes
    /// and every literal segment must appear, IN ORDER, in the text. That is the strongest identity claim a
    /// template supports, and it is still the constant's own text — nothing is spelled here.</para>
    /// <para>A constant that has been emptied cannot make this arm pass silently: an empty or hole-only
    /// constant fails by name, because "contains nothing" is true of every string.</para>
    /// </remarks>
    public static void States(string text, string catalogueConstant)
    {
        Assert.True(catalogueConstant is not null,
            "Facts.States was handed a null sentence. Pass the catalogue constant itself.");

        // A string.Format TEMPLATE doubles every literal brace, so the segments between its holes are not
        // what the render emits until the doubling is undone. Without this, NotReadFraming's own
        // project={{"form": "tree"}} could never be found in a response that carries it.
        var segments = FormatHole.Split(catalogueConstant!)
                                 .Select(s => s.Replace("{{", "{").Replace("}}", "}"))
                                 .Where(s => s.Length > 0)
                                 .ToArray();

        Assert.True(segments.Sum(s => s.Length) > 0,
            "The catalogue sentence handed to Facts.States is empty once its format holes are removed, so " +
            "this arm would pass over any text at all. A sentence emptied to a placeholder is the thing this " +
            "arm is meant to catch, not a reason for it to go quiet.");

        int cursor = 0;
        foreach (var seg in segments)
        {
            var at = text.IndexOf(seg, cursor, StringComparison.Ordinal);
            Assert.True(at >= 0,
                $"This response does not state the catalogue sentence: the segment {Quote(seg)} does not " +
                $"appear{(cursor == 0 ? "" : " after char " + cursor)}. " +
                (segments.Length > 1
                    ? "The sentence is a format template, so its literal segments are required in order; "
                    : "") +
                "Either the surface stopped emitting the sentence, or this fixture does not reach the render " +
                $"site that emits it.\n--- response ---\n{Clip(text)}");
            cursor = at + seg.Length;
        }
    }

    // ---- the walk itself -------------------------------------------------------------------------------

    static readonly Regex FormatHole = new(@"\{\d+\}", RegexOptions.Compiled);

    /// <summary>A FormID as the surfaces spell it — six hex digits, a colon, a plugin filename.</summary>
    static readonly Regex FormIdToken =
        new(@"\b[0-9A-Fa-f]{6}:[^\s""'\],;)]+\.es[pml]\b", RegexOptions.Compiled);

    static List<string> FormIdTokens(string text) =>
        FormIdToken.Matches(text)
                   .Select(m => m.Value)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                   .ToList();

    /// <summary>Every object anywhere under <paramref name="root"/> whose <paramref name="member"/> is
    /// <paramref name="value"/>.</summary>
    static List<JsonElement> NodesKeyedBy(JsonElement root, string member, string value)
    {
        var hits = new List<JsonElement>();
        Walk(root, el =>
        {
            if (el.ValueKind == JsonValueKind.Object
             && el.TryGetProperty(member, out var v)
             && v.ValueKind == JsonValueKind.String
             && string.Equals(v.GetString(), value, StringComparison.OrdinalIgnoreCase))
                hits.Add(el);
        });
        return hits;
    }

    /// <summary>Every value the document carries under <paramref name="member"/> — what a failed navigation
    /// reports instead of "not found".</summary>
    static List<string> IdentityValues(JsonElement root, string member)
    {
        var seen = new List<string>();
        Walk(root, el =>
        {
            if (el.ValueKind == JsonValueKind.Object
             && el.TryGetProperty(member, out var v)
             && v.ValueKind == JsonValueKind.String)
                seen.Add(v.GetString()!);
        });
        return seen.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    static void Walk(JsonElement el, Action<JsonElement> visit)
    {
        visit(el);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) Walk(p.Value, visit);
                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray()) Walk(i, visit);
                break;
        }
    }

    // ---- failure-message shaping -----------------------------------------------------------------------

    const int SampleCap = 12;
    const int ClipChars = 2000;

    static string Sample(IReadOnlyList<string> values) =>
        values.Count == 0
            ? "(nothing)"
            : string.Join(", ", values.Take(SampleCap))
              + (values.Count > SampleCap ? $" (+{values.Count - SampleCap} more)" : "");

    static string Quote(string s) => "\"" + s.Replace("\n", "\\n") + "\"";

    static string Clip(string text) =>
        text.Length <= ClipChars ? text : text[..ClipChars] + $"\n… (+{text.Length - ClipChars} more chars)";
}

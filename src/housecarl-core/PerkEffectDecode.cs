using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

/// <summary>
/// The ONE lenient read houseCARL makes of content Mutagen refused: a PERK entry-point effect Mutagen will not
/// build. Mutagen picks the effect class from the DATA function byte and then demands the EPFT that class writes,
/// so an actor-value function carrying EPFT 1 (Float) throws and takes the whole Effects field — and the whole
/// record — out of every PERK read and scan. xEdit renders such a record because it resolves EPFD's layout from
/// EPFT ALONE and never cross-checks the function byte.
///
/// <para>This decodes the effect list off the record's own bytes the same way, and every caller must say so: the
/// read marks the effect's row, the scan accounts the record. It is bounded three ways, deliberately — it runs ONLY
/// on a PERK, ONLY on the Effects list, and ONLY after the typed getter has already thrown. It is NOT a
/// hand-written PERK schema: it reads the subrecord frame Mutagen itself exposes, it hands the effect's condition
/// block to Mutagen's own <see cref="PerkCondition.CreateFromBinary"/>, and the one table it carries is the EPFD
/// layout per EPFT (<see cref="ParameterShape"/>), which is the delta itself. An EPFT outside that table fails the
/// WHOLE decode, so a record houseCARL cannot account for stays unscannable rather than reading as handled.</para>
///
/// <para>It states what the bytes say and never why they say it: Mutagen refuses such an effect for more than one
/// reason, and naming the cause would be a diagnosis houseCARL has not established. Mutagen's own sentence leads
/// every note this class writes.</para>
/// </summary>
public static class PerkEffectDecode
{
    // The effect list's subrecords, in the order a PERK writes them. PRKE opens an effect and PRKF closes it.
    static readonly RecordType PRKE = new("PRKE");
    static readonly RecordType PRKF = new("PRKF");
    static readonly RecordType DATA = new("DATA");
    static readonly RecordType EPFT = new("EPFT");
    static readonly RecordType EPFD = new("EPFD");
    static readonly RecordType PRKC = new("PRKC");
    static readonly RecordType CTDA = new("CTDA");

    /// <summary>One effect as the raw bytes describe it. <see cref="EntryPoint"/> and <see cref="Function"/> come
    /// off the effect's own DATA and are the two facts the typed read never got to report; the conditions are
    /// Mutagen's own parse of the effect's PRKC/CTDA block, so their links are the ones it would have yielded.</summary>
    public sealed record Effect(
        int Index, byte Type, byte Rank, byte Priority,
        byte? EntryPoint, byte? Function, byte? ConditionTabCount,
        byte? ParameterType, string? Value, FormKey? ValueLink,
        int ConditionCount, IReadOnlyList<FormKey> ConditionLinks, string? ConditionGap);

    /// <summary>What a lenient read of one PERK recovered: every FormKey the record links that houseCARL could
    /// still reach, which effects were refused, and the sentence naming what it could not reach — always together,
    /// so no caller can report the links without the gap.</summary>
    public sealed record LenientRead(IReadOnlyList<FormKey> Links, IReadOnlyList<int> RefusedEffects, string Note);

    /// <summary>How EPFD is laid out for a parameter type: how many bytes, and how to read them. The set is the one
    /// Mutagen's own writer emits (0 no parameter, 1 float, 2 two dwords, 3/4/5 a FormID, 6 a string, 7 a localized
    /// string). Null for any other value — an EPFT houseCARL has no layout for is not guessed at.</summary>
    enum ParameterShape { None, Float, TwoDwords, FormId, Text, LocalizedText }

    static ParameterShape? Shape(byte epft) => epft switch
    {
        0 => ParameterShape.None,
        1 => ParameterShape.Float,
        2 => ParameterShape.TwoDwords,
        3 or 4 or 5 => ParameterShape.FormId,
        6 => ParameterShape.Text,
        7 => ParameterShape.LocalizedText,
        _ => null,
    };

    /// <summary>Decode a PERK's effect list off the record's own bytes. Null when this is not a PERK read from a
    /// binary overlay, when the bytes cannot be reached, or when any part of the list does not decode — a partial
    /// answer here would be exactly the silently-degraded read the design forbids.</summary>
    public static IReadOnlyList<Effect>? Decode(object? record)
    {
        if (record is not IPerkGetter) return null;
        if (Overlay(record) is not { } ov) return null;
        var content = ov.Content;
        var effects = new List<Effect>();

        byte? type = null, rank = null, priority = null, entryPoint = null, function = null, tabCount = null, paramType = null;
        string? value = null;
        FormKey? valueLink = null;
        int condStart = -1, condEnd = -1;
        bool open = false;
        try
        {
            foreach (var sub in RecordSpanExtensions.EnumerateSubrecords(content, GameConstants.SkyrimSE, 0))
            {
                if (sub.RecordType == PRKE)
                {
                    if (open || sub.ContentLength < 3) return null;      // a PRKE inside an open effect is not a shape we decode
                    open = true;
                    type = sub.Content.Span[0]; rank = sub.Content.Span[1]; priority = sub.Content.Span[2];
                    entryPoint = function = tabCount = paramType = null;
                    value = null; valueLink = null; condStart = condEnd = -1;
                    continue;
                }
                if (!open) continue;                                     // the record's own subrecords, ahead of the list
                if (sub.RecordType == PRKC || sub.RecordType == CTDA)
                {
                    if (condStart < 0) condStart = sub.Location;
                    condEnd = sub.Location + sub.TotalLength;
                    continue;
                }
                if (sub.RecordType == PRKF)
                {
                    var (count, links, gap) = ParseConditions(ov, condStart, condEnd);
                    effects.Add(new Effect(effects.Count, type!.Value, rank!.Value, priority!.Value,
                                           entryPoint, function, tabCount, paramType, value, valueLink, count, links, gap));
                    open = false;
                    continue;
                }
                if (sub.RecordType == DATA)
                {
                    // Type 2 (entry point) carries entry point / function / condition-tab count; the quest and
                    // ability arms carry their own payload, which the typed read already handles.
                    if (type == 2 && sub.ContentLength >= 3)
                    { entryPoint = sub.Content.Span[0]; function = sub.Content.Span[1]; tabCount = sub.Content.Span[2]; }
                    continue;
                }
                if (sub.RecordType == EPFT)
                {
                    if (sub.ContentLength != 1) return null;
                    paramType = sub.Content.Span[0];
                    if (Shape(paramType.Value) is null) return null;     // no layout for it — decode nothing
                    continue;
                }
                if (sub.RecordType == EPFD)
                {
                    if (paramType is not { } pt || Shape(pt) is not { } shape) return null;   // EPFD with no EPFT ahead of it
                    (value, valueLink) = ReadParameter(ov, shape, sub.Content);
                    if (value is null && valueLink is null && shape != ParameterShape.None) return null;
                    continue;
                }
            }
        }
        catch { return null; }                                           // anything unexpected in the bytes → no lenient read
        return open ? null : effects;                                    // a PRKE never closed by a PRKF is not a shape we decode
    }

    /// <summary>Read EPFD per its declared shape. Returns (null, null) when the bytes are too short for the shape,
    /// which fails the whole decode rather than reporting a value off bytes that are not there.</summary>
    static (string? Value, FormKey? Link) ReadParameter(OverlayBytes ov, ParameterShape shape, ReadOnlyMemorySlice<byte> data)
    {
        switch (shape)
        {
            case ParameterShape.None:
                return (null, null);
            case ParameterShape.Float:
                if (data.Length < 4) return (null, null);
                return (BitConverter.ToSingle(data.Span[..4]).ToString("R", CultureInfo.InvariantCulture), null);
            case ParameterShape.TwoDwords:
                if (data.Length < 8) return (null, null);
                return (BitConverter.ToSingle(data.Span[..4]).ToString("R", CultureInfo.InvariantCulture)
                        + ", " + BitConverter.ToSingle(data.Span[4..8]).ToString("R", CultureInfo.InvariantCulture)
                        + " (first dword as an integer: " + BitConverter.ToInt32(data.Span[..4]).ToString(CultureInfo.InvariantCulture) + ")", null);
            case ParameterShape.FormId:
                if (data.Length < 4) return (null, null);
                if (ov.Masters is null)
                    return ($"0x{BitConverter.ToUInt32(data.Span[..4]):X8} (raw FormID — this plugin's master list is out of reach, so it is not counted as a link)", null);
                // Mutagen's own reading, defaults included: a reference FormID of all zeroes is FormKey.Null, which
                // is a declared-but-null link and not a link to the first master's record 000000.
                var fk = FormKeyBinaryTranslation.Instance.Parse(data.Span[..4], ov.Masters);
                return (fk.IsNull ? "(null link)" : FormIdToken.Of(fk), fk);
            case ParameterShape.Text:
                return ("\"" + System.Text.Encoding.UTF8.GetString(data.Span).TrimEnd('\0') + "\"", null);
            case ParameterShape.LocalizedText:
                // In a plugin Mutagen opened WITH a strings lookup, these four bytes are a strings-table key, not
                // characters — printing them as text is the silently wrong answer. houseCARL does not know which of
                // the three tables a perk parameter is in, so it hands back the key and says it did not resolve it.
                if (ov.Localized)
                    return (data.Length < 4
                            ? (null, null)
                            : ($"lstring:0x{BitConverter.ToUInt32(data.Span[..4]):X8} (a strings-table key — not resolved here)", null));
                return ("\"" + System.Text.Encoding.UTF8.GetString(data.Span).TrimEnd('\0') + "\"", null);
            default:
                return (null, null);
        }
    }

    /// <summary>The effect's own condition block, parsed by MUTAGEN — <see cref="PerkCondition.CreateFromBinary"/>
    /// over the same PRKC/CTDA bytes, with the parsing meta the record was read with — so which CTDA parameter is a
    /// FormID stays Mutagen's answer and never a table of ours. Returns the condition count, their links, and a
    /// sentence when the block did not parse (the links are then the gap the callers name).</summary>
    static (int Count, IReadOnlyList<FormKey> Links, string? Gap) ParseConditions(OverlayBytes ov, int start, int end)
    {
        if (start < 0 || end <= start) return (0, Array.Empty<FormKey>(), null);
        var links = new List<FormKey>();
        int count = 0;
        try
        {
            using var stream = new MutagenMemoryReadStream(ov.Content.Slice(start, end - start), ov.Meta);
            var frame = new MutagenFrame(stream);
            while (!frame.Complete)
            {
                var block = PerkCondition.CreateFromBinary(frame);
                count += block.Conditions.Count;
                foreach (var l in ((IFormLinkContainerGetter)block).EnumerateFormLinks())
                    if (!l.FormKey.IsNull) links.Add(l.FormKey);
            }
        }
        catch (Exception ex)
        {
            return (count, links, $"its condition block did not parse either ({ex.GetType().Name}: {ex.Message}), so links inside it are not counted");
        }
        return (count, links, null);
    }

    /// <summary>The note a read puts on the row of an effect the typed getter refused: Mutagen's own sentence, then
    /// the bytes themselves. Keeps the read walk's <see cref="ReadEngine.UnreadablePrefix"/> opening so every
    /// consumer classifies the row exactly as before — the row is still not the modeled value, it just now says what
    /// the bytes hold. Null when this record or index is not one the lenient decode covers, so the caller falls back
    /// to the plain fault note.</summary>
    public static string? EffectNote(object? record, int index, string mutagenReason)
    {
        if (Decode(record) is not { } effects || index < 0 || index >= effects.Count) return null;
        var e = effects[index];
        if (e.ParameterType is not { } epft) return null;
        return ReadEngine.UnreadablePrefix + mutagenReason
             + $" — Mutagen refused this effect, so it is read off its own bytes: its entry point is {EntryPointName(e.EntryPoint)}"
             + $", its function byte is {e.Function?.ToString(CultureInfo.InvariantCulture) ?? "?"} (which function that names is the very thing in dispute, so it is left a number)"
             + $", its parameter type EPFT is {epft}, and its parameter value, decoded off EPFT alone as xEdit does, is {e.Value ?? "(none)"}"
             + $". Its {e.ConditionCount} condition(s) "
             + (e.ConditionGap is null ? "were read by Mutagen's own condition parser" : e.ConditionGap)
             + ". That value is the one xEdit shows for this effect; the typed field is not available, so read the effect here rather than through its modeled sub-fields)";
    }

    /// <summary>The entry point's NAME for the marker row. Mutagen models the entry point as one enum, so naming it
    /// is still Mutagen's answer and not a table of ours; a byte outside it is left as the number it is.</summary>
    static string EntryPointName(byte? entryPoint)
    {
        if (entryPoint is not { } b) return "not stated by this effect's DATA";
        var name = Enum.GetName(typeof(APerkEntryPointEffect.EntryType), (APerkEntryPointEffect.EntryType)b);
        return name is null ? $"{b} (a value Mutagen's entry-point enum does not name)" : $"{name} ({b})";
    }

    /// <summary>The lenient link read a scan falls back to when Mutagen's whole-record link walk throws on a PERK.
    /// Reads every modeled field on its own so the parts that DO parse still filter, steps the Effects list by index
    /// so one refused effect does not take its siblings, and takes a refused effect's parameter link and condition
    /// links off the raw decode. Null when the record is not a PERK the decode covers, or when nothing was actually
    /// refused — the caller then treats it exactly as it did before.</summary>
    public static LenientRead? ReadLinks(IMajorRecordGetter record)
    {
        if (Decode(record) is not { } decoded) return null;
        var keys = new List<FormKey>();
        var seen = new HashSet<FormKey>();
        void Add(FormKey fk) { if (!fk.IsNull && seen.Add(fk)) keys.Add(fk); }

        var unreadFields = new List<string>();
        foreach (var name in ReadEngine.ModeledFieldsOf(record))
        {
            if (string.Equals(name, "Effects", StringComparison.Ordinal)) continue;   // stepped by index below
            var (fieldLinks, miss) = ReadEngine.CollectLinksAt(record, new[] { name });
            if (fieldLinks is not null) { foreach (var key in fieldLinks) Add(key); continue; }
            // A field that is simply not link-bearing is not a gap; a field that FAULTED is one, and is named.
            if (miss is not null && miss.StartsWith(ReadEngine.UnreadablePrefix, StringComparison.Ordinal)) unreadFields.Add(name);
        }

        IReadOnlyList<IAPerkEffectGetter> effects;
        int count;
        try { effects = ((IPerkGetter)record).Effects; count = effects.Count; }
        catch { return null; }                                           // even the list's length is out of reach
        var refused = new List<int>();
        var condGaps = new List<string>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (effects[i] is IFormLinkContainerGetter flc)
                    foreach (var l in flc.EnumerateFormLinks()) Add(l.FormKey);
            }
            catch
            {
                refused.Add(i);
                if (i >= decoded.Count) continue;
                if (decoded[i].ValueLink is { } paramLink) Add(paramLink);
                foreach (var condLink in decoded[i].ConditionLinks) Add(condLink);
                if (decoded[i].ConditionGap is { } gap) condGaps.Add($"effect {i}: {gap}");
            }
        }
        if (refused.Count == 0) return null;                             // nothing was refused — the plain walk is the answer

        var note = $"{FormIdToken.Of(record.FormKey)} — effect(s) {string.Join(", ", refused)} were refused by Mutagen and read "
                 + "off their own bytes instead, with the parameter decoded off EPFT alone, as xEdit does, and the "
                 + "conditions parsed by Mutagen's own condition parser"
                 + (condGaps.Count > 0 ? "; except " + string.Join("; ", condGaps) : "")
                 + (unreadFields.Count > 0 ? $"; these field(s) could not be read at all, so their links are missing: {string.Join(", ", unreadFields)}" : "");
        return new LenientRead(keys, refused, note);
    }

    // ---- Reaching the bytes -------------------
    //  Mutagen keeps a binary overlay's own record bytes and its parsing package on internal fields of
    //  PluginBinaryOverlay. There is no public accessor in 0.54.4 and no lenient parse mode, so both are read by
    //  reflection, per record type, and cached. A rename upstream makes the lookup miss, the decode returns null,
    //  and every caller falls back to the behaviour it had before — never a wrong answer, only the old one.

    /// <summary>A record's own bytes and the parsing state it was read with.</summary>
    readonly record struct OverlayBytes(ReadOnlyMemorySlice<byte> Content, ParsingMeta Meta)
    {
        public IReadOnlySeparatedMasterPackage? Masters => Meta.MasterReferences;
        /// <summary>Was this plugin opened WITH a strings lookup? The same test Mutagen's own string reader makes.</summary>
        public bool Localized => Meta.StringsLookup is not null;
    }

    static readonly ConcurrentDictionary<Type, (FieldInfo? Data, FieldInfo? Package)> _fields = new();

    /// <summary>The internal fields for one overlay runtime type; both null when this type is not a binary overlay.
    /// Cached per TYPE, so a record that is not an overlay — a Perk off an in-memory mod the write lanes read back —
    /// cannot disable the decode for every other record in the process.</summary>
    static (FieldInfo? Data, FieldInfo? Package) FieldsFor(Type t) => _fields.GetOrAdd(t, static rt =>
    {
        for (var b = rt; b is not null; b = b.BaseType)
        {
            if (b.Name != "PluginBinaryOverlay") continue;
            return (b.GetField("_recordData", BindingFlags.Instance | BindingFlags.NonPublic),
                    b.GetField("_package", BindingFlags.Instance | BindingFlags.NonPublic));
        }
        return (null, null);
    });

    /// <summary>The record's own subrecord bytes (EDID onward — the major-record header is not part of them) and
    /// the parsing meta they were read with. Null when this record is not a binary overlay, or when Mutagen no
    /// longer holds either where we look.</summary>
    static OverlayBytes? Overlay(object record)
    {
        var (dataField, packageField) = FieldsFor(record.GetType());
        if (dataField is null || packageField is null) return null;
        try
        {
            if (dataField.GetValue(record) is not ReadOnlyMemorySlice<byte> content) return null;
            var pkg = packageField.GetValue(record);
            var meta = pkg?.GetType().GetField("MetaData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pkg);
            return meta is ParsingMeta pm ? new OverlayBytes(content, pm) : null;
        }
        catch { return null; }
    }
}

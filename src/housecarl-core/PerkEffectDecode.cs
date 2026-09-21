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

/// <summary>The ONE lenient read houseCARL makes of content Mutagen refused: a PERK entry-point effect, decoded off the record's own bytes EPFT-first as xEdit does, which every caller must say. It runs only on a PERK, only on Effects, and only after the typed getter has thrown.</summary>
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

    /// <summary>One effect as the raw bytes describe it; the conditions are Mutagen's own parse of the effect's PRKC/CTDA block.</summary>
    public sealed record Effect(
        int Index, byte Type, byte Rank, byte Priority,
        byte? EntryPoint, byte? Function, byte? ConditionTabCount,
        byte? ParameterType, string? Value, FormKey? ValueLink,
        int ConditionCount, IReadOnlyList<FormKey> ConditionLinks, string? ConditionGap);

    /// <summary>What a lenient read of one PERK recovered: the links reached, the effects refused, and the sentence naming what it could not reach — always together.</summary>
    public sealed record LenientRead(IReadOnlyList<FormKey> Links, IReadOnlyList<int> RefusedEffects, string Note);

    /// <summary>How EPFD is laid out for a parameter type; WHICH values exist is Mutagen's own enum, and only the byte layout per member is ours.</summary>
    enum ParameterShape { None, Float, TwoDwords, FormId, Text, LocalizedText }

    static ParameterShape? Shape(byte epft) => (APerkEntryPointEffect.ParameterType)epft switch
    {
        APerkEntryPointEffect.ParameterType.None => ParameterShape.None,
        APerkEntryPointEffect.ParameterType.Float => ParameterShape.Float,
        APerkEntryPointEffect.ParameterType.FloatFloat => ParameterShape.TwoDwords,
        APerkEntryPointEffect.ParameterType.LeveledItem
            or APerkEntryPointEffect.ParameterType.SpellWithStrings
            or APerkEntryPointEffect.ParameterType.Spell => ParameterShape.FormId,
        APerkEntryPointEffect.ParameterType.String => ParameterShape.Text,
        APerkEntryPointEffect.ParameterType.LString => ParameterShape.LocalizedText,
        _ => null,
    };

    /// <summary>Decode a PERK's effect list off the record's own bytes; null when any part of the list does not decode, because a partial answer would be the silently degraded read the design forbids.</summary>
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
                    // Type 2 (entry point) carries entry point / function / condition-tab count; the other arms the typed read already handles.
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

    /// <summary>Read EPFD per its declared shape; bytes too short for the shape fail the whole decode rather than reporting a value off bytes that are not there.</summary>
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
                // Mutagen's own reading, defaults included: an all-zero reference FormID is FormKey.Null, a declared-but-null link.
                var fk = FormKeyBinaryTranslation.Instance.Parse(data.Span[..4], ov.Masters);
                return (fk.IsNull ? "(null link)" : FormIdToken.Of(fk), fk);
            case ParameterShape.Text:
                return ("\"" + System.Text.Encoding.UTF8.GetString(data.Span).TrimEnd('\0') + "\"", null);
            case ParameterShape.LocalizedText:
                // In a plugin opened WITH a strings lookup these four bytes are a table key, not characters, and houseCARL does not resolve it.
                if (ov.Localized)
                    return (data.Length < 4
                            ? (null, null)
                            : ($"lstring:0x{BitConverter.ToUInt32(data.Span[..4]):X8} (a strings-table key — not resolved here)", null));
                return ("\"" + System.Text.Encoding.UTF8.GetString(data.Span).TrimEnd('\0') + "\"", null);
            default:
                return (null, null);
        }
    }

    /// <summary>The effect's own condition block, parsed by MUTAGEN over the same PRKC/CTDA bytes, with a sentence when the block did not parse.</summary>
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

    /// <summary>The note a read puts on the row of an effect the typed getter refused; it keeps <see cref="ReadEngine.UnreadablePrefix"/> so every consumer classifies the row exactly as before.</summary>
    public static string? EffectNote(object? record, int index, string mutagenReason)
    {
        if (Decode(record) is not { } effects || index < 0 || index >= effects.Count) return null;
        var e = effects[index];
        if (e.ParameterType is not { } epft) return null;
        return ReadEngine.UnreadablePrefix + mutagenReason
             + $" — Mutagen refused this effect, so it is read off its own bytes: its entry point is {EntryPointName(e.EntryPoint)}"
             + $", its function byte is {e.Function?.ToString(CultureInfo.InvariantCulture) ?? "?"} (which function that names is the very thing in dispute, so it is left a number)"
             + $", its parameter type EPFT is {ParameterTypeName(epft)}, and its parameter value, decoded off EPFT alone as xEdit does, is {e.Value ?? "(none)"}"
             + $". Its {e.ConditionCount} condition(s) "
             + (e.ConditionGap is null ? "were read by Mutagen's own condition parser" : e.ConditionGap)
             + ". That value is the one xEdit shows for this effect; the typed field is not available, so read the effect here rather than through its modeled sub-fields)";
    }

    /// <summary>The parameter type's NAME for the marker row — Mutagen's own word for the byte; a byte its enum does not name stays a bare number.</summary>
    static string ParameterTypeName(byte epft)
    {
        var name = Enum.GetName(typeof(APerkEntryPointEffect.ParameterType), (APerkEntryPointEffect.ParameterType)epft);
        return name is null ? epft.ToString(CultureInfo.InvariantCulture) : $"{epft} ({name})";
    }

    /// <summary>The entry point's NAME for the marker row, from Mutagen's own enum; a byte outside it is left as the number it is.</summary>
    static string EntryPointName(byte? entryPoint)
    {
        if (entryPoint is not { } b) return "not stated by this effect's DATA";
        var name = Enum.GetName(typeof(APerkEntryPointEffect.EntryType), (APerkEntryPointEffect.EntryType)b);
        return name is null ? $"{b} (a value Mutagen's entry-point enum does not name)" : $"{name} ({b})";
    }

    /// <summary>The lenient link read a scan falls back to when Mutagen's whole-record link walk throws on a PERK; null when the record is not covered, or when nothing was actually refused.</summary>
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

    // Reaching the bytes: Mutagen keeps a binary overlay's own bytes and parsing package on internal fields, read by reflection per record type and cached; a rename upstream makes the decode return null.

    /// <summary>A record's own bytes and the parsing state it was read with.</summary>
    readonly record struct OverlayBytes(ReadOnlyMemorySlice<byte> Content, ParsingMeta Meta)
    {
        public IReadOnlySeparatedMasterPackage? Masters => Meta.MasterReferences;
        /// <summary>Was this plugin opened WITH a strings lookup? The same test Mutagen's own string reader makes.</summary>
        public bool Localized => Meta.StringsLookup is not null;
    }

    static readonly ConcurrentDictionary<Type, (FieldInfo? Data, FieldInfo? Package)> _fields = new();

    /// <summary>The internal fields for one overlay runtime type, cached per TYPE so a record that is not an overlay cannot disable the decode for every other record in the process.</summary>
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

    /// <summary>The record's own subrecord bytes and the parsing meta they were read with; null when this record is not a binary overlay.</summary>
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

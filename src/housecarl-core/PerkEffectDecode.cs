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
/// The ONE lenient read houseCARL makes of content Mutagen refused: a PERK entry-point effect whose DATA function
/// byte and EPFT parameter-type flag disagree (#301). Mutagen picks the effect class from the function byte and
/// then demands the EPFT that class writes, so <c>function 13 (an actor-value mult) + EPFT 1 (Float)</c> throws and
/// takes the whole Effects field — and the whole record — out of every PERK read and scan. xEdit renders the same
/// record because it resolves EPFD's layout from EPFT ALONE and never cross-checks the function byte.
///
/// <para>This decodes the effect list off the record's own bytes the way xEdit does, and every caller must say so:
/// the read marks the effect's row, the scan accounts the record. It is bounded three ways, deliberately — it runs
/// ONLY on a PERK, ONLY on the Effects list, and ONLY after the typed getter has already thrown. It is NOT a
/// hand-written PERK schema: it reads the subrecord frame Mutagen itself exposes, and it decodes only the EPFT
/// layouts Mutagen's own writer emits (<see cref="ParameterLength"/>). An EPFT outside that set fails the WHOLE
/// decode, so a record houseCARL cannot account for stays unscannable rather than reading as leniently handled.</para>
///
/// <para>The one thing it does not reach is the conditions INSIDE an inconsistent effect: deciding which CTDA
/// parameter is a FormID needs the per-function condition schema, which is Mutagen's to model and not ours to
/// hand-write. Every caller states that gap rather than letting a non-match read as proved.</para>
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

    /// <summary>One effect as the raw bytes describe it. <paramref name="Function"/> and <paramref name="EntryPoint"/>
    /// come off the effect's own DATA and are the two facts the typed read never got to report.</summary>
    public sealed record Effect(
        int Index, byte Type, byte Rank, byte Priority,
        byte? EntryPoint, byte? Function, byte? ConditionTabCount,
        byte? ParameterType, string? Value, FormKey? ValueLink, int ConditionCount);

    /// <summary>What a lenient read of one PERK recovered: every FormKey the record links that houseCARL could
    /// still reach, and the sentence naming what it could not — always both, so no caller can report the links
    /// without the gap.</summary>
    public sealed record LenientRead(IReadOnlyList<FormKey> Links, IReadOnlyList<int> InconsistentEffects, string Note);

    /// <summary>How many bytes EPFD holds for a parameter type, and whether those bytes are a FormID — the table
    /// Mutagen's own writer emits (EPFT 0 no parameter, 1 float, 2 two dwords, 3/4/5 a FormID, 6/7 a string).
    /// Null for any other value: an EPFT houseCARL has no layout for is not guessed at.</summary>
    static (int Length, bool IsFormId, bool IsString)? ParameterLength(byte epft) => epft switch
    {
        0 => (0, false, false),
        1 => (4, false, false),
        2 => (8, false, false),
        3 or 4 or 5 => (4, true, false),
        6 or 7 => (-1, false, true),   // -1: the string runs the whole subrecord
        _ => null,
    };

    /// <summary>Decode a PERK's effect list off the record's own bytes. Null when this is not a PERK read from a
    /// binary overlay, when the bytes cannot be reached, or when any part of the list does not decode — a partial
    /// answer here would be exactly the silently-degraded read the design forbids.</summary>
    public static IReadOnlyList<Effect>? Decode(object? record)
    {
        if (record is not IPerkGetter) return null;
        if (RawContent(record) is not { } content) return null;
        var masters = Masters(record);
        var effects = new List<Effect>();

        byte? type = null, rank = null, priority = null, entryPoint = null, function = null, tabCount = null, paramType = null;
        string? value = null;
        FormKey? valueLink = null;
        int conditions = 0;
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
                    value = null; valueLink = null; conditions = 0;
                    continue;
                }
                if (!open) continue;                                     // record-level subrecords before the list
                if (sub.RecordType == PRKF)
                {
                    effects.Add(new Effect(effects.Count, type!.Value, rank!.Value, priority!.Value,
                                           entryPoint, function, tabCount, paramType, value, valueLink, conditions));
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
                if (sub.RecordType == PRKC) { conditions++; continue; }
                if (sub.RecordType == EPFT)
                {
                    if (sub.ContentLength != 1) return null;
                    paramType = sub.Content.Span[0];
                    if (ParameterLength(paramType.Value) is null) return null;   // no layout for it — decode nothing
                    continue;
                }
                if (sub.RecordType == EPFD)
                {
                    if (paramType is not { } pt || ParameterLength(pt) is not { } shape) return null;   // EPFD with no EPFT ahead of it
                    if (shape.IsString)
                    { value = "\"" + System.Text.Encoding.UTF8.GetString(sub.Content.Span).TrimEnd('\0') + "\""; continue; }
                    if (sub.ContentLength < shape.Length) return null;
                    if (shape.Length == 0) { value = null; continue; }
                    if (shape.IsFormId)
                    {
                        uint raw = BitConverter.ToUInt32(sub.Content.Span[..4]);
                        valueLink = masters is null ? null : FormKeyBinaryTranslation.Instance.Parse(sub.Content.Span[..4], masters, false, false);
                        value = valueLink is { } fk && !fk.IsNull ? FormIdToken.Of(fk)
                              : masters is null ? $"0x{raw:X8} (raw FormID — this plugin's master list is out of reach)"
                              : FormIdToken.Of(valueLink!.Value);
                        continue;
                    }
                    value = shape.Length == 4
                        ? BitConverter.ToSingle(sub.Content.Span[..4]).ToString("R", CultureInfo.InvariantCulture)
                        : BitConverter.ToSingle(sub.Content.Span[..4]).ToString("R", CultureInfo.InvariantCulture)
                          + ", " + BitConverter.ToSingle(sub.Content.Span[4..8]).ToString("R", CultureInfo.InvariantCulture)
                          + " (first dword as an integer: " + BitConverter.ToInt32(sub.Content.Span[..4]).ToString(CultureInfo.InvariantCulture) + ")";
                    continue;
                }
            }
        }
        catch { return null; }                                           // anything unexpected in the bytes → no lenient read
        return open ? null : effects;                                    // a PRKE never closed by a PRKF is not a shape we decode
    }

    /// <summary>The note a read puts on the row of an effect the typed getter refused: Mutagen's own sentence, the
    /// three bytes that disagree, and what the value was decoded off. Keeps the read walk's
    /// <see cref="ReadEngine.UnreadablePrefix"/> opening so every consumer classifies the row exactly as before —
    /// the row is still not the modeled value, it just now says what the bytes hold. Null when this record or
    /// index is not one the lenient decode covers, so the caller falls back to the plain fault note.</summary>
    public static string? EffectNote(object? record, int index, string mutagenReason)
    {
        if (Decode(record) is not { } effects || index < 0 || index >= effects.Count) return null;
        var e = effects[index];
        if (e.ParameterType is not { } epft) return null;
        return ReadEngine.UnreadablePrefix + mutagenReason
             + $" — internally inconsistent: this effect's DATA names entry point {e.EntryPoint?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
             + $" and function byte {e.Function?.ToString(CultureInfo.InvariantCulture) ?? "?"}, while EPFT says {epft}"
             + $"; the parameter was decoded off EPFT alone, as xEdit does: {e.Value ?? "(no parameter)"}"
             + $"; {e.ConditionCount} condition(s) inside this effect are not walked, so their links do not reach references=)";
    }

    /// <summary>The lenient link read a scan falls back to when Mutagen's whole-record link walk throws on a PERK.
    /// Reads every modeled field on its own so the parts that DO parse still filter, steps the Effects list by
    /// index so one refused effect does not take its siblings, and takes the refused effect's own parameter link
    /// off the raw decode. Null when the record is not a PERK the decode covers — the caller then treats it exactly
    /// as it did before, unscannable and accounted.</summary>
    public static LenientRead? ReadLinks(IMajorRecordGetter record)
    {
        if (Decode(record) is not { } decoded) return null;
        var keys = new List<FormKey>();
        var seen = new HashSet<FormKey>();
        void Add(FormKey fk) { if (!fk.IsNull && seen.Add(fk)) keys.Add(fk); }

        var unread = new List<int>();
        foreach (var name in ReadEngine.ModeledFieldsOf(record))
        {
            if (string.Equals(name, "Effects", StringComparison.Ordinal)) continue;   // stepped by index below
            var (links, _) = ReadEngine.CollectLinksAt(record, new[] { name });       // a non-link field just answers null
            if (links is not null) foreach (var fk in links) Add(fk);
        }

        IReadOnlyList<IAPerkEffectGetter> effects;
        int count;
        try { effects = ((IPerkGetter)record).Effects; count = effects.Count; }
        catch { return null; }                                           // even the list's length is out of reach
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (effects[i] is IFormLinkContainerGetter flc)
                    foreach (var l in flc.EnumerateFormLinks()) Add(l.FormKey);
            }
            catch
            {
                unread.Add(i);
                if (i < decoded.Count && decoded[i].ValueLink is { } fk) Add(fk);
            }
        }
        if (unread.Count == 0) return null;                              // nothing was refused — the plain walk is the answer

        var note = $"{FormIdToken.Of(record.FormKey)} — effect(s) {string.Join(", ", unread)} carry a function byte and an "
                 + "EPFT that disagree; they were decoded off EPFT alone, as xEdit does, so the effect's own parameter link "
                 + "counts here but the conditions inside it are not walked";
        return new LenientRead(keys, unread, note);
    }

    // ---- Reaching the bytes -------------------
    //  Mutagen keeps a binary overlay's own record bytes and its parsing package on internal fields of
    //  PluginBinaryOverlay. There is no public accessor in 0.54.4 and no lenient parse mode, so both are read by
    //  reflection, once, and cached. A rename upstream makes these null, the decode returns null, and every caller
    //  falls back to the behaviour it had before — never a wrong answer, only the old one.

    static FieldInfo? _recordData, _package;
    static bool _resolved;

    static void Resolve(object record)
    {
        if (_resolved) return;
        for (var t = record.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Name != "PluginBinaryOverlay") continue;
            _recordData = t.GetField("_recordData", BindingFlags.Instance | BindingFlags.NonPublic);
            _package = t.GetField("_package", BindingFlags.Instance | BindingFlags.NonPublic);
            break;
        }
        _resolved = true;
    }

    /// <summary>The record's own subrecord bytes (EDID onward — the major-record header is not part of it), or null
    /// when this record is not a binary overlay or Mutagen no longer holds them where we look.</summary>
    static ReadOnlyMemorySlice<byte>? RawContent(object record)
    {
        Resolve(record);
        if (_recordData is null) return null;
        try { return _recordData.GetValue(record) is ReadOnlyMemorySlice<byte> d ? d : null; }
        catch { return null; }
    }

    /// <summary>The master package the containing plugin was parsed with, so a raw FormID in EPFD becomes the same
    /// FormKey Mutagen would have produced (light and medium masters included). Null when it cannot be reached —
    /// the decode then renders the raw FormID and contributes no link, saying so.</summary>
    static IReadOnlySeparatedMasterPackage? Masters(object record)
    {
        Resolve(record);
        if (_package is null) return null;
        try
        {
            var pkg = _package.GetValue(record);
            var meta = pkg?.GetType().GetField("MetaData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pkg);
            return (meta as ParsingMeta)?.MasterReferences;
        }
        catch { return null; }
    }
}

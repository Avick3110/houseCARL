using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The one runtime-layer target resolver: every format turns a token into a record here, under its own row's
/// <see cref="TargetSpelling"/> — which side of the separator the FormID is on, what the written number means, whether
/// bare hex or a plugin name is a term of its own, and which records an EditorID is looked up among. SkyPatcher's
/// spelling is one row among them. Rules and sources: docs/architecture/skypatcher-layer.md.</summary>
public static class RuntimeTargets
{
    /// <summary>SkyPatcher's own spelling, from its row of the runtime-format source.</summary>
    static TargetSpelling Sky => RuntimeFormats.SkyPatcher.Targets;

    /// <summary>The scope string an every-type lookup passes: the class every Skyrim record is one of, which is the one map the game keeps names in.</summary>
    public static readonly string EveryTypeScope = nameof(SkyrimMajorRecord);

    /// <summary>What a token names under one format's spelling.</summary>
    public enum TargetKind
    {
        /// <summary>A FormID written beside a plugin name.</summary>
        Address,
        /// <summary>Hex with no plugin: the runtime FormID the game, the console and the logs print.</summary>
        RuntimeFormId,
        /// <summary>A plugin name, which names a file and never a record.</summary>
        PluginName,
        /// <summary>A name looked up among records.</summary>
        EditorId,
        /// <summary>A token past the separator for a format whose row has not read what such a token is; never a claim about it.</summary>
        Unread,
    }

    /// <summary>One token read under a format's spelling: what it names, and the parts it names it with.</summary>
    public sealed record TargetToken(TargetKind Kind, string Raw, FormAddress? Address, uint? FormId, string? Name)
    {
        /// <summary>True when the written FormID carries more than a 24-bit object id, so it holds a load index of its own.</summary>
        public bool LoadIndexWritten => FormId is { } v && v > FormIdRange.ObjectIdMask;
    }

    // ---- reading a token ----------------------------------------------------------------

    /// <summary>Read one token under <paramref name="s"/>, in the frameworks' own order: the separator first, then a
    /// plugin term, then bare hex, else a name. A row that has not read what a format makes of a token past the
    /// separator says so, rather than falling through to a name.</summary>
    public static TargetToken Read(string token, TargetSpelling s)
    {
        token ??= "";
        if (TryParseAddress(token, s) is { } a) return new TargetToken(TargetKind.Address, token, a, a.FormId, null);
        if (Unread(s)) return new TargetToken(TargetKind.Unread, token, null, null, token);
        if (IsPluginName(token, s)) return new TargetToken(TargetKind.PluginName, token, null, null, token);
        if (s.BareFormId == TargetBareFormId.RuntimeFormId && TryParseBareHex(token, out var raw))
            return new TargetToken(TargetKind.RuntimeFormId, token, null, raw, null);
        return new TargetToken(TargetKind.EditorId, token, null, null, token);
    }

    /// <summary>Whether this row leaves any rule for a token past the separator unread, which makes every such token unread rather than a guess.</summary>
    static bool Unread(TargetSpelling s)
        => s.PluginTerm == TargetPluginTerm.Unread || s.BareFormId == TargetBareFormId.Unread
           || s.MissingName == TargetMissingName.Unread || s.EditorIds == TargetEditorIdScope.Unread;

    /// <summary>A token holding the spelling's separator, read as that spelling writes it; null for a token without
    /// one. A FormID side the framework cannot read leaves <see cref="FormAddress.FormId"/> null.</summary>
    public static FormAddress? TryParseAddress(string token, TargetSpelling s)
    {
        if (string.IsNullOrEmpty(token)) return null;
        int at = token.IndexOf(s.Separator);
        if (at < 0) return null;
        // The plugin name is taken as written, untrimmed, as the frameworks' mod lookups take it.
        return s.AddressOrder == TargetAddressOrder.PluginFirst
            ? new FormAddress(token[..at], ParseHex(token[(at + 1)..]), EditorId: null)
            : new FormAddress(token[(at + 1)..], ParseHex(token[..at]), EditorId: null);
    }

    /// <summary>Whether a term is a plugin name under this spelling: the extension text anywhere in it, or at its end, each as written.</summary>
    public static bool IsPluginName(string token, TargetSpelling s) => s.PluginTerm switch
    {
        TargetPluginTerm.ContainsExtensionText => s.PluginExtensions.Any(e => token.Contains(e, StringComparison.Ordinal)),
        TargetPluginTerm.EndsWithExtension => s.PluginExtensions.Any(e => token.EndsWith(e, StringComparison.Ordinal)),
        _ => false,
    };

    /// <summary>Hex with no plugin, as the frameworks read one: a <c>0x</c> or <c>0X</c> prefix is required and every character after it is a hex digit.</summary>
    public static bool TryParseBareHex(string token, out uint value)
    {
        value = 0;
        if (token.Length <= 2 || token[0] != '0' || (token[1] != 'x' && token[1] != 'X')) return false;
        return token[2..].All(Uri.IsHexDigit)
               && uint.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The FormID side read like the frameworks' base-16 <c>stoul</c>: leading whitespace, an optional <c>0x</c>, then hex up to the first non-hex character, but no sign; null when no digit is read or the value passes 32 bits.</summary>
    public static uint? ParseHex(string side)
    {
        int i = 0;
        while (i < side.Length && char.IsWhiteSpace(side[i])) i++;
        if (i + 2 < side.Length && side[i] == '0' && side[i + 1] is 'x' or 'X' && Uri.IsHexDigit(side[i + 2])) i += 2;
        int start = i;
        while (i < side.Length && Uri.IsHexDigit(side[i])) i++;
        if (i == start) return null;
        return ulong.TryParse(side[start..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v <= uint.MaxValue
            ? (uint)v : null;
    }

    // ---- resolving a token --------------------------------------------------------------

    /// <summary>The record a token names under <paramref name="s"/>, scoped for an EditorID as the spelling says; null for a plugin name, an unread rule, or a miss.</summary>
    public static FormKey? Resolve(string token, string? scope, SkyPatcherOverlay.IFormResolver resolver, TargetSpelling s)
        => Resolve(Read(token, s), scope, resolver, s);

    /// <summary>The record a read token names; null for a plugin name, an unread rule, or a miss.</summary>
    public static FormKey? Resolve(TargetToken t, string? scope, SkyPatcherOverlay.IFormResolver resolver, TargetSpelling s)
        => t.Kind switch
        {
            TargetKind.Address => AddressKey(t.Address!, resolver, s),
            TargetKind.RuntimeFormId => resolver.FromRuntimeFormId(t.FormId!.Value).Record,
            TargetKind.EditorId => resolver.ResolveEditorId(t.Raw, EditorIdScope(scope, s)),
            _ => null,
        };

    /// <summary>Which records an EditorID is looked up among: the call's scope, or every type for a format reading the game's one map.</summary>
    static string? EditorIdScope(string? scope, TargetSpelling s)
        => s.EditorIds == TargetEditorIdScope.EveryType ? EveryTypeScope : scope;

    /// <summary>An address to its record, under the spelling's arithmetic: the record's own local object id, or the written number added to the plugin's runtime base as the game's own lookup adds it.</summary>
    static FormKey? AddressKey(FormAddress a, SkyPatcherOverlay.IFormResolver resolver, TargetSpelling s)
    {
        if (a.Plugin is null || a.FormId is null) return null;
        if (s.FormId == TargetFormIdMath.LocalObjectId)
            return SkyPatcherOverlay.TryFormKey(a, out var fk) ? fk : null;
        return RuntimeAdd(a, resolver) is { } runtime ? resolver.FromRuntimeFormId(runtime).Record : null;
    }

    /// <summary>The runtime FormID the game's own lookup builds for a plugin and a written number: the plugin's runtime base plus the number, with no mask, so a number carrying a load index lands outside the plugin. Null when the plugin is not in the active order or the sum passes 32 bits.</summary>
    public static uint? RuntimeAdd(FormAddress a, SkyPatcherOverlay.IFormResolver resolver)
    {
        if (a.Plugin is null || a.FormId is null || resolver.RuntimeBaseOf(a.Plugin) is not { } b) return null;
        ulong sum = (ulong)b + a.FormId.Value;
        return sum > uint.MaxValue ? null : (uint)sum;
    }

    // ---- SkyPatcher's own doors, on SkyPatcher's row ------------------------------------

    /// <summary>The record a value names, scoped to a Mutagen type or type group for an EditorID; null for a name literal or a miss.</summary>
    public static FormKey? Resolve(SkyPatcherValue v, string? scope, SkyPatcherOverlay.IFormResolver resolver)
        => v.IsNameLiteral ? null : v.Address is { } a ? FormKeyOf(a) : resolver.ResolveEditorId(v.Raw, scope);

    /// <summary>The record one sub-arg token names, by the same rule.</summary>
    public static FormKey? Resolve(string token, string? scope, SkyPatcherOverlay.IFormResolver resolver)
        => Resolve(token, scope, resolver, Sky);

    /// <summary>The record a value names in the first of <paramref name="scopes"/> that resolves it, as a folder with two record types resolves a target.</summary>
    public static FormKey? Resolve(SkyPatcherValue v, IEnumerable<string?> scopes, SkyPatcherOverlay.IFormResolver resolver)
        => scopes.Select(s => Resolve(v, s, resolver)).FirstOrDefault(k => k is not null);

    /// <summary>The same for one token.</summary>
    public static FormKey? Resolve(string token, IEnumerable<string?> scopes, SkyPatcherOverlay.IFormResolver resolver)
        => scopes.Select(s => Resolve(token, s, resolver)).FirstOrDefault(k => k is not null);

    /// <summary>A '|' address to its FormKey; null for one whose FormID side or plugin name cannot be read.</summary>
    public static FormKey? FormKeyOf(FormAddress a) => SkyPatcherOverlay.TryFormKey(a, out var fk) ? fk : null;

    /// <summary>True when the value is looked up by EditorID: no '|' and not a <c>~name~</c> literal.</summary>
    public static bool ByEditorId(SkyPatcherValue v) => !v.IsNameLiteral && v.Address is null;

    /// <summary>What one target says on its line: why it names nothing, or the duplicate rule when its EditorID names more
    /// than one record; null for a target that names one record. Worded after <c>target 'X' (in a K) </c>.</summary>
    public static string? Report(SkyPatcherValue v, string? scope, SkyPatcherOverlay.IFormResolver resolver)
    {
        if (v.IsNameLiteral) return null;
        if (v.Address is { } a)
        {
            if (a.IsMalformed)
                return "is not a FormID SkyPatcher can read (no hex digit after '|', or more than 32 bits), and SkyPatcher never reads a '|' token as an EditorID, so it names nothing";
            if (!resolver.PluginPresent(a.Plugin!))
                return a.Plugin!.Trim() != a.Plugin && resolver.PluginPresent(a.Plugin!.Trim())
                    ? $"names plugin '{a.Plugin}' with a space SkyPatcher keeps (it does not trim around '|'), so it names nothing"
                    : $"names plugin '{a.Plugin}', which is not in the active order, so it names nothing";
            return FormKeyOf(a) is { } fk && resolver.WinnerPluginOf(fk) is null
                ? $"names {FormIdToken.Of(fk)}, which no plugin in the active order defines, so it names nothing"
                : null;
        }
        if (resolver.EditorIdGap(scope) is { } gap) return $"finds nothing: {gap}";
        return resolver.EditorIdNote(v.Raw, scope)
            ?? (resolver.ResolveEditorId(v.Raw, scope) is null
                ? $"finds no record by that EditorID{(scope is null ? "" : $" among {scope} records")} in the active order"
                : null);
    }

    // ---- what one token says on its line, under any row ---------------------------------

    /// <summary>What a token says on a line of <paramref name="format"/>: why it names nothing, what it names that is
    /// not a record, the rule the row has not read for this format, or null when it names one record plainly.</summary>
    public static string? Report(string token, string? scope, SkyPatcherOverlay.IFormResolver resolver, DistributorFormat format)
    {
        var s = format.Targets;
        var t = Read(token, s);
        switch (t.Kind)
        {
            case TargetKind.Address:
                var a = t.Address!;
                if (a.IsMalformed)
                    return $"is not a FormID {format.Name} can read (no hex digit, or more than 32 bits), so it names nothing";
                if (!resolver.PluginPresent(a.Plugin!))
                    return $"names plugin '{a.Plugin}', which is not in the active order, so it names nothing";
                if (s.FormId == TargetFormIdMath.LocalObjectId)
                    return FormKeyOf(a) is { } local && resolver.WinnerPluginOf(local) is null
                        ? $"names {FormIdToken.Of(local)}, which no plugin in the active order defines, so it names nothing" : null;
                var runtime = RuntimeAdd(a, resolver);
                if (runtime is null)
                    return $"names a FormID past the 32 bits {format.Name} adds it into, so it names nothing";
                var (record, why) = resolver.FromRuntimeFormId(runtime.Value);
                var index = t.LoadIndexWritten
                    ? $"is written with a load index ('{a.FormId!.Value:X}' is more than an object id), which {format.Name} adds to '{a.Plugin}'s own index instead of masking off, so it names {RuntimeFormId.Format(runtime.Value)}"
                    : null;
                return record is null ? Join(index, why) : index;

            case TargetKind.RuntimeFormId:
                var (bare, bareWhy) = resolver.FromRuntimeFormId(t.FormId!.Value);
                return bare is null ? bareWhy : null;

            case TargetKind.PluginName:
                return resolver.PluginPresent(t.Name!)
                    ? $"names plugin '{t.Name}', not a record"
                    : $"names plugin '{t.Name}', which is not in the active order";

            case TargetKind.Unread:
                return $"carries no separator, and what {format.Name} makes of such a token is not read yet: its lane states it";

            default:
                var scoped = EditorIdScope(scope, s);
                if (resolver.EditorIdGap(scoped) is { } gap) return $"finds nothing: {gap}";
                if (resolver.ResolveEditorId(t.Raw, scoped) is not null) return resolver.EditorIdNote(t.Raw, scoped);
                return s.MissingName == TargetMissingName.CreatesKeyword
                    ? $"finds no record by that EditorID in the active order, so {format.Name} creates a keyword of that name"
                    : "finds no record by that EditorID in the active order";
        }
    }

    static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + "; " + b;
}

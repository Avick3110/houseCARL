using System.Text.Json;

namespace HousecarlCore;

/// <summary>The distributor rows of the runtime-format source: SPID, KID, BOS and FLM, each saying where its files are
/// found and how a target token in it is spelt. The lines themselves — kinds, conflicts, labels — are each format's own
/// lane; a value the code has no rule for fails the load, as SkyPatcher's row does.</summary>
public static partial class RuntimeFormats
{
    static IReadOnlyList<DistributorFormat>? _distributors;

    /// <summary>The distributor rows of the embedded source, in the order the source lists them (memoized).</summary>
    public static IReadOnlyList<DistributorFormat> Distributors => _distributors ??= DistributorsFrom(SourceText());

    /// <summary>One distributor row by its <c>distributor=</c> key; null when the source has no such row.</summary>
    public static DistributorFormat? Distributor(string key)
        => Distributors.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parse the distributor rows from source text; a value the code has no rule for throws, naming the row and the value.</summary>
    public static IReadOnlyList<DistributorFormat> DistributorsFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("distributors").EnumerateArray().Select(DistributorRow).ToList();
    }

    static DistributorFormat DistributorRow(JsonElement row)
    {
        var key = Str(row, "key", "distributors");
        var ctx = "distributors." + key;
        var folders = row.GetProperty("folders").EnumerateArray().Select(f => Folder(f, ctx + ".folders")).ToList();
        if (folders.Count == 0) throw new InvalidOperationException($"runtime-format source [{ctx}]: 'folders' is empty.");
        return new DistributorFormat(key, Str(row, "format", ctx), Str(row, "version", ctx), folders, Targets(row.GetProperty("targets"), ctx + ".targets"),
            Lines(row, key, ctx));
    }

    static DistributorFolder Folder(JsonElement f, string ctx)
        => new(AssetResolver.Normalize(Str(f, "root", ctx)),
               Str(f, "nameToken", ctx), Case(f, "tokenCase", ctx),
               Str(f, "extension", ctx), Case(f, "extensionCase", ctx),
               Str(f, "order", ctx) switch
               {
                   "sortedOrdinal" => true,
                   "filesystemOrder" => false,
                   var v => throw Unknown(ctx, "order", v),
               },
               Str(f, "depth", ctx) switch
               {
                   "topLevelOnly" => false,
                   "descends" => true,
                   var v => throw Unknown(ctx, "depth", v),
               });

    static StringComparison Case(JsonElement el, string prop, string ctx)
        => Str(el, prop, ctx) switch
        {
            "ordinal" => StringComparison.Ordinal,
            "ordinalIgnoreCase" => StringComparison.OrdinalIgnoreCase,
            var v => throw Unknown(ctx, prop, v),
        };

    /// <summary>One row's target spelling; <paramref name="separator"/> is the character the row states elsewhere, as SkyPatcher's grammar block holds its address character.</summary>
    static TargetSpelling Targets(JsonElement t, string ctx, char? separator = null)
        => new(Str(t, "addressOrder", ctx) switch
               {
                   "formIdFirst" => TargetAddressOrder.FormIdFirst,
                   "pluginFirst" => TargetAddressOrder.PluginFirst,
                   var v => throw Unknown(ctx, "addressOrder", v),
               },
               separator ?? Single(Str(t, "separator", ctx), ctx + ".separator"),
               Str(t, "formId", ctx) switch
               {
                   "runtimeAdd" => TargetFormIdMath.RuntimeAdd,
                   "runtimeAddTrimmedAtTen" => TargetFormIdMath.RuntimeAddTrimmedAtTen,
                   "localObjectId" => TargetFormIdMath.LocalObjectId,
                   var v => throw Unknown(ctx, "formId", v),
               },
               Str(t, "bareFormId", ctx) switch
               {
                   "runtimeFormId" => TargetBareFormId.RuntimeFormId,
                   "notAFormId" => TargetBareFormId.NotAFormId,
                   "unread" => TargetBareFormId.Unread,
                   var v => throw Unknown(ctx, "bareFormId", v),
               },
               Str(t, "pluginTerm", ctx) switch
               {
                   "containsExtensionText" => TargetPluginTerm.ContainsExtensionText,
                   "endsWithExtension" => TargetPluginTerm.EndsWithExtension,
                   "notATerm" => TargetPluginTerm.NotATerm,
                   "unread" => TargetPluginTerm.Unread,
                   var v => throw Unknown(ctx, "pluginTerm", v),
               },
               Strs(t, "pluginExtensions", ctx),
               Str(t, "missingName", ctx) switch
               {
                   "createsKeyword" => TargetMissingName.CreatesKeyword,
                   "byPosition" => TargetMissingName.ByPosition,
                   "namesNothing" => TargetMissingName.NamesNothing,
                   "unread" => TargetMissingName.Unread,
                   var v => throw Unknown(ctx, "missingName", v),
               },
               Str(t, "editorIds", ctx) switch
               {
                   "everyType" => TargetEditorIdScope.EveryType,
                   "scoped" => TargetEditorIdScope.Scoped,
                   "unread" => TargetEditorIdScope.Unread,
                   var v => throw Unknown(ctx, "editorIds", v),
               });
}

/// <summary>One distributor format's row: the folders it reads, how a target token in it is spelt, and, once its reader
/// has landed, its line grammar (<see cref="Lines"/>; SPID's today, null for a format whose lines are not read yet).</summary>
public sealed record DistributorFormat(
    string Key,
    string Name,
    string Version,
    IReadOnlyList<DistributorFolder> Folders,
    TargetSpelling Targets,
    SpidGrammar? Lines);

/// <summary>One folder a format reads: where it is under Data, which names it takes there, whether the DLL sorts that
/// folder's list, and whether the read descends into subfolders.</summary>
public sealed record DistributorFolder(
    string Root,
    string NameToken,
    StringComparison TokenCompare,
    string Extension,
    StringComparison ExtensionCompare,
    bool Sorted,
    bool Descends)
{
    /// <summary>Whether the format reads a file of this name out of this folder: the extension, then the name token,
    /// each under this folder's own comparison. Upstream tests the token against the whole path; the name is what
    /// this test is given, so a folder that descends matches a token written in a subfolder's name only by the file's
    /// own name.</summary>
    public bool Reads(string fileName)
        => Path.GetExtension(fileName).Equals(Extension, ExtensionCompare)
           && (NameToken.Length == 0 || fileName.Contains(NameToken, TokenCompare));
}

/// <summary>How one format spells a target: the address shape and its FormID arithmetic, whether a bare FormID or a
/// plugin name is a term of its own, and what it does with a name no record holds. <c>Unread</c> is a rule this build
/// has not read for that format, never a claim about it.</summary>
public sealed record TargetSpelling(
    TargetAddressOrder AddressOrder,
    char Separator,
    TargetFormIdMath FormId,
    TargetBareFormId BareFormId,
    TargetPluginTerm PluginTerm,
    IReadOnlyList<string> PluginExtensions,
    TargetMissingName MissingName,
    TargetEditorIdScope EditorIds);

/// <summary>Which side of the separator the FormID is written on.</summary>
public enum TargetAddressOrder { PluginFirst, FormIdFirst }

/// <summary>What the written FormID means: the record's local object id, a number added to the plugin's runtime base
/// as the game's own lookup adds it, or that add with the written load index erased from a ten-character side first.</summary>
public enum TargetFormIdMath { LocalObjectId, RuntimeAdd, RuntimeAddTrimmedAtTen }

/// <summary>What a token of hex with no plugin names: nothing, the runtime FormID, or a rule not read for this format.</summary>
public enum TargetBareFormId { NotAFormId, RuntimeFormId, Unread }

/// <summary>What makes a term a plugin name: nothing, the extension text anywhere in it, the extension at its end, or a rule not read for this format.</summary>
public enum TargetPluginTerm { NotATerm, ContainsExtensionText, EndsWithExtension, Unread }

/// <summary>What a name no record holds does: nothing, the framework creating a keyword of that name, an outcome the
/// line's own position decides (its reader says it, the resolver only that the name finds nothing), or a rule not read
/// for this format.</summary>
public enum TargetMissingName { NamesNothing, CreatesKeyword, ByPosition, Unread }

/// <summary>Which records an EditorID is looked up among: the call's scope, every type as the game's own map keeps them, or a rule not read for this format.</summary>
public enum TargetEditorIdScope { Scoped, EveryType, Unread }

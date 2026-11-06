using System.Collections;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>What a list parameter's file may hold: the artifact identity it takes, and whether a plain file splits
/// on commas as well as line breaks.</summary>
public sealed record ListKind(string Identity, bool CommaSeparates, string Example)
{
    /// <summary>FormIDs: a plain list (commas or lines) or a <c>formid</c> artifact, epoch-checked by the consumer.</summary>
    public static readonly ListKind FormId = new("formid", true, "C:\\work\\list.jsonl");
    /// <summary>Asset paths or globs: lines, a JSON array, or a <c>path</c> artifact.</summary>
    public static readonly ListKind Path = new("path", false, "C:\\work\\list.txt");
    /// <summary>Plugin filenames: lines or a JSON array.</summary>
    public static readonly ListKind Plugin = new("plugin", false, "C:\\work\\list.txt");
    /// <summary>Names: record types, field paths, families, predicates; lines or a JSON array.</summary>
    public static readonly ListKind Name = new("name", false, "C:\\work\\list.txt");
}

/// <summary>The sentence each list parameter's text carries, saying what file its <c>["@&lt;absolute path&gt;"]</c> takes.</summary>
public static class ListText
{
    const string In = " Takes [\"@<absolute path>\"] in place of the list: ";
    public const string FormIds = In + "a file of FormIDs, one per line or comma-separated, or a to_file= artifact whose identity column is 'formid', epoch-checked against the current build.";
    public const string Paths = In + "a file of paths, one per line or a JSON array, or a " + ToolNames.AssetStatus + " to_file= artifact (identity column 'path'), not epoch-checked.";
    public const string Plugins = In + "a file with one plugin filename per line, each line read as written; a filename that itself starts with '@' is written '@@' inline.";
    public const string Names = In + "a file with one entry per line, each line read as written; an entry that itself starts with '@' is written '@@' inline.";
}

/// <summary>A list-valued tool parameter as the caller sent it. Its entries are read only through
/// <c>ListDoor.Read</c>, so every list goes through the one <c>@file</c> door; contract in
/// docs/architecture/output-and-artifacts.md, "Re-entering an artifact".</summary>
public sealed class ListArg : ICollection<string>
{
    readonly List<string> _entries = new();

    public ListArg() { }
    ListArg(IEnumerable<string> entries) => _entries.AddRange(entries);

    /// <summary>A C# caller passes the list as an array.</summary>
    public static implicit operator ListArg?(string[]? entries) => entries is null ? null : new ListArg(entries);

    /// <summary>The entries as sent, for the door alone.</summary>
    internal IReadOnlyList<string> Entries => _entries;

    int ICollection<string>.Count => _entries.Count;
    bool ICollection<string>.IsReadOnly => false;
    void ICollection<string>.Add(string item) => _entries.Add(item);
    void ICollection<string>.Clear() => _entries.Clear();
    bool ICollection<string>.Contains(string item) => _entries.Contains(item);
    void ICollection<string>.CopyTo(string[] array, int arrayIndex) => _entries.CopyTo(array, arrayIndex);
    bool ICollection<string>.Remove(string item) => _entries.Remove(item);
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();
}

/// <summary>One list read: the tokens (null when the parameter was unset), the artifact demand the consumer must
/// epoch-check, the echo of the file it came from, or the refusal.</summary>
public readonly record struct ListRead(string[]? Tokens, ArtifactDemand? Demand, string? EchoSource, string? Error);

/// <summary>The list door: the one reader behind every list parameter, typed (<see cref="ListArg"/>) or a spec
/// object's own string-array member.</summary>
public static class ListDoor
{
    /// <summary>Read an unset-or-sent list; an unset one reads as null tokens.</summary>
    /// <param name="param">The parameter's name, as refusals spell it.</param>
    /// <param name="spelling">How a caller writes the parameter, <c>{0}</c> standing for the list, when not <c>name=[…]</c>.</param>
    /// <param name="negation">A sigil an entry may lead with, carried onto every token a negated file expands to.</param>
    public static ListRead Read(this ListArg? arg, string param, ListKind kind, string? spelling = null, char? negation = null)
        => ReadItems(arg?.Entries, param, kind, spelling, negation);

    /// <summary>Read a list held as an array, the spec objects' members: inline entries with '@@' unescaped, or the
    /// whole list from the one '@' file entry; a <paramref name="negation"/>-led '@file' entry negates every token.</summary>
    public static ListRead ReadItems(IReadOnlyList<string>? items, string param, ListKind kind, string? spelling = null, char? negation = null)
    {
        if (items is null) return default;
        var label = param + "=";
        var alone = spelling is null ? $"{param}=[\"@<path>\"]" : string.Format(spelling, "\"@<path>\"");
        bool NegatedFile(string? i) => negation is { } s && i?.TrimStart() is { Length: > 1 } n && n[0] == s && ListFile.NamesFile(n[1..]);
        if (items.Any(NegatedFile))
        {
            if (items.Count > 1) return Refused(ListFile.Mixed(label, alone));
            var inner = ReadItems(new[] { items[0].TrimStart()[1..].TrimStart() }, param, kind, spelling);
            return inner.Error is not null ? inner
                : new(inner.Tokens!.Select(t => negation + t.Trim()).ToArray(), inner.Demand, negation + inner.EchoSource, null);
        }
        if (!items.Any(ListFile.NamesFile)) return new(items.Select(i => ListFile.Unescape(i)!).ToArray(), null, null, null);
        if (items.Count > 1) return Refused(ListFile.Mixed(label, alone));
        var (path, content, rerr) = ListFile.Read(items[0], label, alone, kind.Example);
        if (rerr is not null) return Refused(rerr);

        if (ResultArtifact.LooksLikeArtifact(content!))
        {
            var (tokens, demand, aerr) = ListFile.ReadArtifact(path!, content!, kind.Identity, label);
            if (aerr is not null) return Refused(aerr);
            return new(tokens!.ToArray(), demand, "@" + path, null);
        }

        var plain = Artifacts.SplitListTokens(content!, kind.CommaSeparates).ToArray();
        if (plain.Length == 0) return Refused(ListFile.Empty(label, path!));
        return new(plain, null, "@" + path, null);
    }

    static ListRead Refused(string message) => new(null, null, null, "error: " + message);
}

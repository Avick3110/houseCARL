namespace HousecarlCore;

/// <summary>The <c>@file</c> list convention's one reader: which entry names a file, the <c>@@</c> escape, and every
/// refusal for the file itself, worded once for every list that takes one; contract in
/// docs/architecture/output-and-artifacts.md, "Re-entering an artifact".</summary>
public static class ListFile
{
    /// <summary>True when the entry names a list file: a leading '@' that is not the '@@' escape.</summary>
    public static bool NamesFile(string? entry)
        => entry is not null && entry.TrimStart() is { Length: > 0 } t && t[0] == '@' && !(t.Length > 1 && t[1] == '@');

    /// <summary>The entry as the value it stands for: '@@x' is the literal '@x', anything else as written.</summary>
    public static string? Unescape(string? entry)
        => entry is not null && entry.TrimStart() is { Length: > 1 } t && t[0] == '@' && t[1] == '@' ? t[1..] : entry;

    /// <summary>The refusal for an '@file' entry beside inline entries.</summary>
    public static string Mixed(string label, string alone)
        => $"{label} mixes an '@file' entry with inline entries — '@<path>' stands IN PLACE OF the whole list. " +
           $"Pass {alone} alone, or put every entry in the file.";

    /// <summary>The refusal for a list file that holds no entry.</summary>
    public static string Empty(string label, string path)
        => $"{label} list file '{path}' is empty — put one entry per line in it, or pass the list inline.";

    /// <summary>Read the file an '@' entry names: the path must follow the '@', be absolute, be readable and hold
    /// something; each failure is one sentence naming <paramref name="label"/>.</summary>
    /// <param name="alone">How the caller writes this list as one '@' entry, for the no-path remedy.</param>
    /// <param name="example">An absolute example path of the file this list takes.</param>
    public static (string? Path, string? Content, string? Error) Read(string entry, string label, string alone, string example)
    {
        var path = entry.TrimStart().Substring(1).Trim().Trim('"', '\'');
        if (path.Length == 0)
            return (null, null, $"{label} '@' names a list file but no path follows it — pass {alone}.");
        if (PathArguments.NotAbsolute(path, $"{label} list file", "the file the list is in", example) is { } notAbsolute)
            return (null, null, notAbsolute);
        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception ex) { return (null, null, $"could not read {label} list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(content.Trim('﻿')))
            return (null, null, Empty(label, path));
        return (path, content, null);
    }

    /// <summary>A result artifact's identity column as the list, refused by name when its identity is not
    /// <paramref name="identity"/>; the demand is the consumer's to epoch-check.</summary>
    public static (List<string>? Tokens, ArtifactDemand? Demand, string? Error) ReadArtifact(
        string path, string content, string identity, string label)
    {
        var (manifest, tokens, err) = ResultArtifact.ReadIdentity(path, content);
        if (err is not null) return (null, null, err);
        if (!manifest!.Identity!.Equals(identity, StringComparison.OrdinalIgnoreCase))
            return (null, null, $"artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' identities, " +
                                $"not '{identity}' ones — there is no {identity} list in it for {label}.");
        return (tokens, new ArtifactDemand(path, manifest.Epoch), null);
    }
}

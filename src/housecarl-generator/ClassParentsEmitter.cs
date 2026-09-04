using System.Text.Json;
using System.Text.RegularExpressions;

namespace HousecarlGenerator;

/// <summary>
/// Emits vanilla-class-parents.json — the decompiler's baseline child→parent class map, generated
/// from the vanilla Papyrus sources' own `ScriptName X extends Y` headers. The asset ships beside
/// the exe (csproj Content item) because it CANNOT be regenerated on a fresh checkout: vanilla
/// sources install with the Creation Kit, not the game, and not on CI. Regenerate manually on a
/// game update; the output is sorted so regeneration diffs are stable.
///
/// Why it exists: implicit-UPCAST suppression in <see cref="HousecarlCore.PapyrusDecompiler"/>
/// needs ancestor chains, and the load-bearing ones are vanilla's (Actor → ObjectReference →
/// Form). Without the map, output stays CORRECT but keeps explicit casts.
///
/// Run: dotnet run --project src/housecarl-generator class-parents "&lt;vanilla Source\Scripts&gt;" &lt;out.json&gt;
/// </summary>
internal static class ClassParentsEmitter
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: class-parents <vanilla Source\\Scripts dir> <out.json>");
            return 2;
        }
        var srcDir = args[0].Trim('"');
        var outPath = args[1].Trim('"');
        if (!Directory.Exists(srcDir))
        {
            Console.Error.WriteLine($"no such directory: {srcDir}");
            return 2;
        }

        var extendsRx = new Regex(@"^\s*ScriptName\s+(\S+)\s+extends\s+(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var edges = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int files = 0, unreadable = 0;
        foreach (var f in Directory.EnumerateFiles(srcDir, "*.psc"))
        {
            files++;
            try
            {
                foreach (var line in File.ReadLines(f).Take(20))
                {
                    var m = extendsRx.Match(line);
                    if (!m.Success) continue;
                    if (!edges.TryAdd(m.Groups[1].Value, m.Groups[2].Value)
                        && !edges[m.Groups[1].Value].Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                        Console.Error.WriteLine($"  CONFLICT: {m.Groups[1].Value} extends both {edges[m.Groups[1].Value]} and {m.Groups[2].Value} — keeping the first");
                    break;
                }
            }
            catch { unreadable++; }
        }

        var doc = new
        {
            generatedFrom = "vanilla Papyrus sources (Data\\Source\\Scripts, installed by the Creation Kit) — ScriptName-extends headers",
            edgeCount = edges.Count,
            edges,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{files} .psc scanned ({unreadable} unreadable), {edges.Count} class-parent edges → {outPath}");
        return 0;
    }
}

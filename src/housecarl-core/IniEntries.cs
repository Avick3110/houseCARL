namespace HousecarlCore;

/// <summary>The key=value entries of an SKSE-style INI file with the section each sits in: ';' and '#' lines are comments, a section is the text inside its brackets (so '[Patcher] ; note' is 'Patcher'), and an inline ';' comment is cut off the value.</summary>
public static class IniEntries
{
    public static IEnumerable<(string Section, string Key, string Value)> Read(string path)
    {
        string section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = close > 0 ? line[1..close].Trim() : "";
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            yield return (section, line[..eq].Trim(), line[(eq + 1)..].Split(';')[0].Trim());
        }
    }
}

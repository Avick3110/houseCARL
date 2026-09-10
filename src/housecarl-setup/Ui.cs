using System.Text;

namespace HousecarlSetup;

/// <summary>
/// Everything the setup run puts on screen: the banner, the step log, the notes, the problem blocks and the
/// one prompt. It lives here so <see cref="Program"/> stays the install logic and presentation is one file to
/// read and one file to change.
///
/// Hand-rolled on purpose. The setup exe ships single-file, trimmed and self-contained, with zero package
/// references, and the notices generator only walks the server publish - a console library here would ship
/// unattributed. Colour is set through <see cref="Console.ForegroundColor"/> (no escape codes in the text), so
/// a run whose output is captured gets the same characters a coloured run does.
///
/// Colour is off when the stream is redirected or NO_COLOR is set, per https://no-color.org.
/// </summary>
public static class Ui
{
    private const int RuleWidth = 63;

    // The label column of the detection block and the menu, wide enough for the longest label either prints.
    private const int RowLabelWidth = 31;

    private static readonly bool NoColor =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    private static readonly bool OutColor = !NoColor && !Console.IsOutputRedirected;
    private static readonly bool ErrColor = !NoColor && !Console.IsErrorRedirected;

    // A double-clicked console runs an OEM code page, which has no em dash, so the sentences below would go
    // out best-fit mapped. A redirected stream is left alone (its reader chose the encoding), and a run with
    // no console handle throws here, which is not a reason to stop installing.
    static Ui()
    {
        if (Console.IsOutputRedirected && Console.IsErrorRedirected) return;
        try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
        catch (Exception) { }
    }

    /// <summary>The header: the product name, its version and what this program is.</summary>
    public static void Banner(string version)
    {
        Console.WriteLine();
        Console.Write("  ");
        Paint(OutColor, ConsoleColor.White, () => Console.Write("houseCARL"));
        Paint(OutColor, ConsoleColor.DarkGray, () => Console.Write(" " + version + "  ·  setup"));
        Console.WriteLine();
        Rule();
        Console.WriteLine();
    }

    /// <summary>A horizontal divider the width of the banner.</summary>
    public static void Rule()
        => Paint(OutColor, ConsoleColor.DarkGray, () => Console.WriteLine("  " + new string('─', RuleWidth)));

    /// <summary>A section title: the detection block, the plan.</summary>
    public static void Heading(string text)
    {
        Console.WriteLine();
        Paint(OutColor, ConsoleColor.White, () => Console.WriteLine("  " + text));
        Console.WriteLine();
    }

    /// <summary>One thing that was looked for, and what was found - the rows of the detection block.</summary>
    public static void Row(string label, string value)
    {
        Console.Write("    " + label.PadRight(RowLabelWidth));
        Paint(OutColor, ConsoleColor.DarkGray, () => Console.WriteLine(value));
    }

    /// <summary>One option in the pick-a-host menu, with what was detected beside it.</summary>
    public static void MenuItem(string key, string label, string note)
    {
        if (note.Length == 0) { Console.WriteLine("    [" + key + "] " + label); return; }
        Console.Write("    [" + key + "] " + label.PadRight(RowLabelWidth - 4));
        Paint(OutColor, ConsoleColor.DarkGray, () => Console.WriteLine(note));
    }

    /// <summary>A host's heading in the plan, and whether this run installs or upgrades it.</summary>
    public static void PlanHost(string host, string action)
    {
        Paint(OutColor, ConsoleColor.Cyan, () => Console.Write("    " + host));
        Paint(OutColor, ConsoleColor.DarkGray, () => Console.WriteLine("  ·  " + action));
    }

    /// <summary>One destination in the plan: the exact path, and what goes there.</summary>
    public static void PlanLine(string path, string what, int pathWidth)
    {
        Console.Write("      " + path.PadRight(pathWidth));
        Paint(OutColor, ConsoleColor.DarkGray, () => Console.WriteLine("   " + what));
    }

    /// <summary>Prose at the body indent, one line each, with a single blank line before the first.</summary>
    public static void Body(params string[] lines)
    {
        Console.WriteLine();
        foreach (string line in lines)
            Console.WriteLine("  " + line);
    }

    /// <summary>One step of the run: what is being done for which host, and the paths it touches.</summary>
    public static void Step(string host, string what, params string[] paths)
    {
        Paint(OutColor, ConsoleColor.Cyan, () => Console.Write("[" + host + "] "));
        Console.WriteLine(what);
        foreach (string path in paths)
            Console.WriteLine("      -> " + path);
    }

    /// <summary>An item under the step above it.</summary>
    public static void Bullet(string text) => Console.WriteLine("      - " + text);

    /// <summary>
    /// A run that did not do what was asked: one plain sentence saying what went wrong and what to try, then
    /// the detail - paths, links, the file that is in the way - indented under it. Goes to stderr, because it
    /// is the failure.
    /// </summary>
    public static void Problem(string sentence, params string[] details)
    {
        Console.Error.WriteLine();
        Paint(ErrColor, ConsoleColor.Red, () => Console.Error.WriteLine("  " + sentence));
        WriteDetails(Console.Error, details);
    }

    /// <summary>
    /// The same sentence-first shape for something that did not stop the run - a step that was skipped, or a
    /// leftover the cleanup could not take. Goes to stdout, because the install still happened.
    /// </summary>
    public static void Note(string sentence, params string[] details)
    {
        Console.WriteLine();
        Paint(OutColor, ConsoleColor.Yellow, () => Console.WriteLine("  " + sentence));
        WriteDetails(Console.Out, details);
    }

    /// <summary>Ask a question and read the answer; null when there is no interactive input.</summary>
    public static string? Prompt(string question)
    {
        Console.Write(question);
        return Console.ReadLine();
    }

    // An empty detail line is a deliberate blank between paragraphs, so it is written without the indent.
    private static void WriteDetails(TextWriter w, string[] details)
    {
        if (details.Length > 0) w.WriteLine();
        foreach (string d in details)
            w.WriteLine(d.Length == 0 ? "" : "      " + d);
        w.WriteLine();
    }

    // Colour is a console attribute, not text, so the writer sees the same characters either way.
    private static void Paint(bool colored, ConsoleColor color, Action write)
    {
        if (!colored) { write(); return; }
        ConsoleColor prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            write();
        }
        finally { Console.ForegroundColor = prev; }
    }
}

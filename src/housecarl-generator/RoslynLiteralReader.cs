using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HousecarlGenerator;

/// <summary>
/// READER A — the C# compiler's own tokenizer, asked what string literals a file contains.
///
/// <para><b>Why the compiler rather than a hand lexer here.</b> <c>description-vocab-guard</c>'s net is "every
/// string literal in the shipped source trees", and the set of string literals in a C# file is not a matter of
/// opinion — it is what the compiler decides. Roslyn IS that decision, so this reader cannot disagree with the
/// build about what a literal is, what an escape decodes to, or where an interpolation hole begins. The second
/// design's hand lexer got exactly those wrong (a literal inside a hole was invisible; an apostrophe inside such a
/// literal flipped it into character-literal mode and swallowed the rest of the file) and every arm stayed green,
/// because the only thing checking the lexer was the lexer.</para>
///
/// <para><b>What it is NOT.</b> It is not the completeness proof. A reader that certifies itself certifies
/// nothing, which is the failure both prior designs died of — so this reader's output is held against
/// <see cref="HandLiteralLexer"/>'s, written independently from C#'s lexical grammar and sharing no code with it
/// (<see cref="SourceLiteral"/> is a data shape, not a tokenizer). <c>INV6-AGREE</c> is the arm; either reader
/// stopping early makes the two disagree and turns it red, naming the file.</para>
///
/// <para><b>Parsing, not compiling.</b> No references are resolved and no semantic model is built — this is a
/// syntax parse of one file at a time, and it dominates the guard's cost: the whole run over the shipped surface
/// takes about two seconds end to end rather than milliseconds. The parse is
/// run at <see cref="LanguageVersion.Preview"/> so a language feature newer than the pinned package is a PARSE
/// ERROR that <c>INV6-PARSE</c> reports by name, rather than a construct silently misread into fewer literals.</para>
/// </summary>
public static class RoslynLiteralReader
{
    static readonly CSharpParseOptions Options = new(LanguageVersion.Preview);

    /// <summary>Every string literal in <paramref name="src"/>, with the compiler's own decoding.
    /// <paramref name="parseErrors"/> receives one line per ERROR-severity diagnostic — a file that does not parse
    /// is a file whose literals are not trustworthy, and saying so is the difference between a stated gap and a
    /// silent one.</summary>
    public static List<SourceLiteral> Read(string src, out List<string> parseErrors)
    {
        var tree = CSharpSyntaxTree.ParseText(src, Options);
        parseErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} at line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
            .ToList();

        var outp = new List<SourceLiteral>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            // The hole count is the depth by definition: an InterpolationSyntax ancestor IS an enclosing hole.
            int depth = node.Ancestors().OfType<InterpolationSyntax>().Count();
            string? text = node switch
            {
                // Plain, verbatim, raw and u8 literals are one token, and ValueText is the value the compiler
                // builds from it — including the raw-string indentation strip, which is a rule about the SOURCE
                // that no consumer of the string can see afterwards.
                LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
                                              || lit.IsKind(SyntaxKind.Utf8StringLiteralExpression)
                    => lit.Token.ValueText,
                // An interpolated string is authored text with values dropped into it. The text segments are the
                // authored part; the holes are not, and are marked rather than guessed at.
                InterpolatedStringExpressionSyntax interp => Interpolated(interp),
                _ => null,
            };
            if (text is null) continue;
            var span = node.Span;
            outp.Add(new SourceLiteral(
                tree.GetLineSpan(span).StartLinePosition.Line + 1, depth, text, span.Start, span.End));
        }
        return outp;
    }

    /// <summary>The authored text of an interpolated string, with each hole marked.
    /// <para>The doubled braces are collapsed here rather than taken from the token. A text segment's
    /// <c>ValueText</c> resolves backslash escapes but leaves <c>{{</c> and <c>}}</c> as written — brace
    /// un-doubling happens later, when the compiler lowers the interpolation — so the token's value is not yet the
    /// string the program prints. Found by <c>INV6-AGREE</c> on shipped GraphQL and JSON-shaped messages, which is
    /// the disagreement that arm exists to produce.</para>
    /// <para><b>Only for the flavours that HAVE escapes.</b> Doubling is how a brace is spelled in the two regular
    /// interpolated forms, so there a doubled brace can only BE an escape and the collapse is exact. A RAW
    /// interpolated string escapes nothing: its hole opens on a run of as many braces as it has dollar signs, and
    /// any shorter run is ordinary content — so collapsing there would hand back a value the compiler never
    /// builds, and INV6-AGREE would red on correct source naming neither reader. The start token says which
    /// flavour this is, so the question is answered by the parse rather than assumed.</para></summary>
    static string Interpolated(InterpolatedStringExpressionSyntax interp)
    {
        bool doubles = interp.StringStartToken.IsKind(SyntaxKind.InterpolatedStringStartToken)
                    || interp.StringStartToken.IsKind(SyntaxKind.InterpolatedVerbatimStringStartToken);
        var sb = new System.Text.StringBuilder();
        foreach (var part in interp.Contents)
            sb.Append(part is InterpolatedStringTextSyntax t
                ? (doubles ? t.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}") : t.TextToken.ValueText)
                : SourceLiteral.HoleMarker);
        return sb.ToString();
    }
}

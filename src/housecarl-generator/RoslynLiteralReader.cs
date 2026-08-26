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
        => Read(src, out parseErrors, out _);

    /// <summary>The same read, plus the <see cref="AppendCall"/> table taken from the SAME parse — so a caller
    /// that needs both pays for one tree rather than two.</summary>
    public static List<SourceLiteral> Read(string src, out List<string> parseErrors,
                                           out Dictionary<int, AppendCall> appendCalls)
    {
        var tree = CSharpSyntaxTree.ParseText(src, Options);
        parseErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} at line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
            .ToList();

        var root = tree.GetRoot();
        var outp = new List<SourceLiteral>();
        foreach (var node in root.DescendantNodes())
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
        appendCalls = AppendCalls(root);
        return outp;
    }

    /// <summary>The text-adding calls in a file, keyed by the END offset of the literal each one takes — the key
    /// a merged run carries forward, so a run's TAIL is what gets looked up.
    /// <para><b>Why the tree and not the text in front of the literal.</b> Until 2026-08-26 the merge asked a
    /// regex what stood before a literal and read a receiver NAME out of it, which is a guess at a receiver
    /// rather than the receiver. It got two shipped shapes wrong, both measured: a call with a VALUE argument
    /// earlier in the chain (<c>sb.Append(count).Append("a"); sb.Append("b");</c>) left a <c>)</c> where the
    /// pattern wanted a name, and an INDEXER-spelled receiver (<c>cells[i].Sb</c>) could not be spelled by an
    /// identifier pattern at all. Both refused a run this guard PRINTS that it reads, so a phrase split across
    /// one reached a caller with INV1 green. Asking the syntax tree instead makes both correct by construction
    /// rather than by two more alternations: the head receiver is a NODE, whatever it is spelled like, and a
    /// chain is a parent relation rather than a window of characters.</para>
    /// <para>This is READER A's knowledge alone, which is why <see cref="AppendCall"/> lives here and not beside
    /// <see cref="SourceLiteral"/>: reader B never sees it, and <c>INV6-AGREE</c> compares the two readers'
    /// LITERALS, below and before any merging. Nothing here can make the two agree.</para></summary>
    static Dictionary<int, AppendCall> AppendCalls(SyntaxNode root)
    {
        var map = new Dictionary<int, AppendCall>();
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // A call with no receiver expression adds text to nothing this can name, so it is not a run link.
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            var verb = Array.Find(RunMethods, m => m.Name == ma.Name.Identifier.ValueText);
            if (verb.Name is null) continue;
            // ONE argument, and it must BE the literal: a second argument is a value between the two halves, and
            // a named or by-ref argument is not the text-adding shape either. "Be" rather than "end with" —
            // `sb.Append(name == "…")` closes on a string and appends a bool, so its literal is read by nobody,
            // and keying that call at the literal's end would merge text no caller sees. Measured: admitting an
            // expression that merely ends on a literal joins 114 further pairs in the shipped trees.
            var args = inv.ArgumentList.Arguments;
            if (args.Count != 1 || args[0].NameColon is not null
                || !args[0].RefKindKeyword.IsKind(SyntaxKind.None)) continue;
            var arg = args[0].Expression;
            if (arg is not LiteralExpressionSyntax and not InterpolatedStringExpressionSyntax) continue;

            // The chain's HEAD receiver — sb, cells[i].Sb, Console — reached by walking down the receiver chain
            // rather than by matching characters. `inner` is the link this call is made ON, when there is one.
            var recv = ma.Expression;
            int inner = recv is InvocationExpressionSyntax link ? link.Span.End : -1;
            while (recv is InvocationExpressionSyntax i2 && i2.Expression is MemberAccessExpressionSyntax ma2)
                recv = ma2.Expression;

            // The statement this chain stands in, and the one after it: two calls are consecutive when one
            // statement follows the other, which is a sibling relation and not a gap of characters.
            SyntaxNode outer = inv;
            while (outer.Parent is MemberAccessExpressionSyntax up && up.Expression == outer
                                                                  && up.Parent is InvocationExpressionSyntax upi)
                outer = upi;
            int statement = -1, following = -1;
            if (outer.Parent is ExpressionStatementSyntax st)
            {
                statement = st.Span.Start;
                following = FollowingStatementStart(st);
            }

            map[arg.Span.End] = new AppendCall(
                verb.Adds, verb.EndsLine, HeadText(recv), inv.Span.End, inner,
                ((InvocationExpressionSyntax)outer).Span.End, statement, following);
        }
        return map;
    }

    /// <summary>The calls that put a literal in front of a caller with nothing between, each mapped to the verb
    /// it IS and to whether it breaks the line afterwards.
    /// <para>A <c>Line</c> variant is the same verb wearing a terminator: <c>AppendLine("b")</c> appends "b" and
    /// THEN breaks, so <c>Append("a"); AppendLine("b");</c> is read as "ab" on one line and is one sentence,
    /// while <c>AppendLine("a"); Append("b");</c> puts the break between the halves and is two. Which side the
    /// break falls on is the whole distinction, and it is a property of the call rather than of the pair, so it
    /// is recorded here and the merge asks for it.</para></summary>
    static readonly (string Name, string Adds, bool EndsLine)[] RunMethods =
    {
        ("Append", "Append", false), ("AppendLine", "Append", true),
        ("Write",  "Write",  false), ("WriteLine",  "Write",  true),
    };

    /// <summary>Where the next statement in the same body begins, or -1 when this one is last — or stands alone
    /// as an <c>if</c> body, where what follows the <c>if</c> is not a continuation of anything.</summary>
    static int FollowingStatementStart(StatementSyntax st)
    {
        SyntaxNode node = st.Parent is GlobalStatementSyntax g ? g : st;
        if (node.Parent is null) return -1;
        bool seen = false;
        foreach (var sibling in node.Parent.ChildNodes())
        {
            if (seen) return (sibling is GlobalStatementSyntax gs ? (SyntaxNode)gs.Statement : sibling).Span.Start;
            if (sibling == node) seen = true;
        }
        return -1;
    }

    /// <summary>A receiver expression as one comparable string. The common case carries no whitespace and is
    /// taken as written; a receiver broken across lines goes through the compiler's own formatter, so a chain
    /// wrapped at its dot compares equal to the same chain written on one line instead of splitting a run.</summary>
    static string HeadText(SyntaxNode recv)
    {
        var raw = recv.ToString();
        foreach (var ch in raw)
            if (char.IsWhiteSpace(ch)) return recv.NormalizeWhitespace().ToString();
        return raw;
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

/// <summary>
/// One text-adding call — <c>sb.Append("…")</c>, <c>Console.Write("…")</c> — as READER A's syntax tree sees it.
/// This is what lets a run of them be recognised as ONE authored sentence without asking a regex what stood in
/// front of a literal.
/// </summary>
/// <param name="Adds">The text-adding verb this call IS — <c>Append</c> or <c>Write</c>. A <c>Line</c> variant
/// reports the same verb as its plain form, because it adds the same text; where they differ is
/// <see cref="EndsLine"/>.</param>
/// <param name="EndsLine">Whether the call breaks the line AFTER its own text. Such a call can FINISH a run — the
/// two halves still read as one line — but never continue one, because the break then lands between them.</param>
/// <param name="Receiver">The HEAD of the invocation chain, as text — <c>sb</c>, <c>cells[i].Sb</c>,
/// <c>Console</c>. Two calls are on the same receiver when these are equal; an indexer or a dotted path is just
/// another node here, which is the whole point of taking it from the tree.</param>
/// <param name="Node">This invocation's END offset — its identity. The end rather than the start, because every
/// link of a fluent chain starts at the same character.</param>
/// <param name="Inner">The end offset of the invocation this call is chained ONTO, or -1 when the receiver is not
/// a call. <c>Inner</c> equal to another entry's <see cref="Node"/> is the fluent-run relation, exactly; and
/// <c>Inner</c> of -1 says this call is the HEAD of its chain, so nothing in the chain ran before it.</param>
/// <param name="Outer">The end offset of the outermost invocation of this chain. <c>Outer</c> equal to
/// <see cref="Node"/> says nothing in the chain runs after this call — which is what makes it the chain's last
/// contribution, and the only position from which a run may continue into the NEXT statement.</param>
/// <param name="Statement">The start offset of the expression statement this chain stands in, or -1 when the
/// chain is not a statement on its own.</param>
/// <param name="Following">The start offset of the next statement in the same body, or -1. Equality with another
/// entry's <see cref="Statement"/> is the statement-run relation — which is why an intervening <c>if</c>, or any
/// other statement, breaks a run by construction rather than by a pattern that has to enumerate it.</param>
public readonly record struct AppendCall(string Adds, bool EndsLine, string Receiver, int Node, int Inner,
                                         int Outer, int Statement, int Following);

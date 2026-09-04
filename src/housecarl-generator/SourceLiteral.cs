namespace HousecarlGenerator;

/// <summary>
/// One string literal as a reader hands it back: the decoded value the compiler would build, plus the source span
/// that lets adjacent literals be recognised as one authored sentence.
///
/// <para>This record is the ONLY thing <see cref="RoslynLiteralReader"/> and <see cref="HandLiteralLexer"/> may
/// share. <c>description-vocab-guard</c> certifies its completeness by the two readers agreeing, so they must not
/// share any code that decides what a literal IS — a shared data shape is fine, a shared tokenization helper is
/// not.</para>
/// </summary>
/// <param name="Line">1-based line the literal starts on — for the <c>path:line</c> an author is sent to.</param>
/// <param name="Depth">How many interpolation holes enclose this literal. 0 is ordinary top-level text; 1 is a
/// literal inside <c>$"… {cond ? "arm" : "other"} …"</c>. Only depth-0 literals take part in the <c>+</c>-run
/// merge — a literal inside a hole is its own sentence, because what surrounds it is an expression, not prose.</param>
/// <param name="Text">The DECODED value: escapes resolved, verbatim doubling collapsed, raw-string indentation
/// stripped — the string the compiler builds. An interpolated literal keeps its authored text with each hole
/// rendered as <see cref="HoleMarker"/>, because what fills a hole at runtime is a value, not a claim.</param>
/// <param name="Start">Source offset of the first character of the literal, prefix (<c>$</c>, <c>@</c>) included.</param>
/// <param name="End">Source offset one past the literal's closing quote.</param>
public readonly record struct SourceLiteral(int Line, int Depth, string Text, int Start, int End)
{
    /// <summary>What both readers put in place of an interpolation hole. It must carry no letters, so it can never
    /// manufacture a phrase the author did not write, and must stay visible in an excerpt. A phrase split across a
    /// hole is therefore not matched, exactly as one split across <c>"a" + x + "b"</c> is not — a declared boundary
    /// of the guard.</summary>
    public const string HoleMarker = "{…}";
}

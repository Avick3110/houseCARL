namespace HousecarlGenerator;

/// <summary>
/// One string literal as a READER hands it back: the decoded value the compiler would build, plus the source span
/// that lets adjacent literals be recognised as one authored sentence.
///
/// <para>This record is the ONLY thing <see cref="RoslynLiteralReader"/> and <see cref="HandLiteralLexer"/> share.
/// That is deliberate and load-bearing: <c>description-vocab-guard</c>'s completeness claim is certified by the
/// two readers AGREEING, and an agreement between two spellings that share their tokenization would certify
/// nothing (#386 — both prior designs died of a completeness claim checked by the machinery it certifies). A data
/// shape is not tokenization; a helper either of them called to decide what a literal IS would be.</para>
/// </summary>
/// <param name="Line">1-based line the literal starts on — for the <c>path:line</c> an author is sent to.</param>
/// <param name="Depth">How many interpolation HOLES enclose this literal. 0 is ordinary top-level text; 1 is a
/// literal inside <c>$"… {cond ? "arm" : "other"} …"</c>, which is the shape the second design could not see at
/// all. Only depth-0 literals take part in the <c>+</c>-run merge — a literal inside a hole is its own sentence,
/// because what surrounds it is an expression rather than prose.</param>
/// <param name="Text">The DECODED value: escapes resolved, verbatim doubling collapsed, raw-string indentation
/// stripped — the string the compiler builds. An interpolated literal keeps its authored text with each hole
/// rendered as <see cref="HoleMarker"/>, because what fills a hole at runtime is a value, not a claim.</param>
/// <param name="Start">Source offset of the first character of the literal, prefix (<c>$</c>, <c>@</c>) included.</param>
/// <param name="End">Source offset one past the literal's closing quote.</param>
public readonly record struct SourceLiteral(int Line, int Depth, string Text, int Start, int End)
{
    /// <summary>What both readers put in place of an interpolation hole. Carries no letters, so it can never
    /// manufacture a phrase that the author did not write; visible in an excerpt, so a reader of a violation can
    /// see where the value went. The consequence — a phrase split ACROSS a hole is not matched, exactly as a
    /// phrase split across <c>"a" + x + "b"</c> is not — is a declared boundary of the guard, not an accident.</summary>
    public const string HoleMarker = "{…}";
}

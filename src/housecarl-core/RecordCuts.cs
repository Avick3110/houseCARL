namespace HousecarlCore;

/// <summary>The records one answer's deep reads cut at their line budget, counted where the answer counts its
/// rows so the top of the answer can say how many, which, and at what budget.</summary>
public sealed class RecordCuts
{
    /// <summary>How many names a statement shown inline lists before it says "and N more".</summary>
    public const int ShownNames = 3;

    readonly List<(string FormId, string Label)> _cut = new();
    bool _compared;

    /// <summary>The budget the cut records ran under; one per call, so the first cut sets it.</summary>
    public int Budget { get; private set; }

    public int Count => _cut.Count;

    /// <summary>The cut records' FormIDs, in the order they were counted.</summary>
    public IReadOnlyList<string> FormIds => _cut.Select(c => c.FormId).ToList();

    /// <summary>Count <paramref name="r"/> when its read was cut.</summary>
    public void Add(RecordFields? r)
    {
        if (r?.CutAt is { } at) Tally(r.FormKey, r.EditorId, at);
    }

    /// <summary>Count a comparison row whose side's read was cut at <paramref name="budget"/>.</summary>
    public void AddCompared(string formId, string? editorId, int budget)
    {
        _compared = true;
        Tally(formId, editorId, budget);
    }

    void Tally(string formId, string? editorId, int budget)
    {
        if (_cut.Count == 0) Budget = budget;
        _cut.Add((formId, editorId is { Length: > 0 } e ? $"{formId} ({e})" : formId));
    }

    /// <summary>The statement naming the first <paramref name="shown"/> cut records, or null when none was cut.</summary>
    public string? Sentence(int shown = ShownNames) =>
        _cut.Count == 0 ? null
            : ReadEngine.CutSentence(_cut.Count, Budget, _cut.Take(shown).Select(c => c.Label).ToList(), _compared);
}

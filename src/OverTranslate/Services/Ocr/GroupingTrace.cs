using OverTranslate.Services;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Names every line one grouping pass saw, so that a diagnostic can say which lines it is talking
/// about rather than quoting their text.
/// </summary>
/// <remarks>
/// <para>Text is not an identifier. It repeats, it gets truncated to fit a column, and the same
/// words appear at both ends of a pass — once as a block the detector drew and once as a line built
/// out of several of them. A report that names its lines by what they say cannot be cross-read
/// between its own layers, which is exactly what a report about a wrong verdict has to be.</para>
///
/// <para>Two namespaces on purpose. A detected block is <c>b0</c>, a line the grouper works on is
/// <c>L0</c>, and a line built from one block alone is both — the merge keeps the instance it was
/// handed. Giving it one id would silently pick a side; giving it two says what happened.</para>
///
/// <para>Identity is by reference, not by value: two blocks with the same text and the same box are
/// two lines on the page, and a record's own equality would fold them into one.</para>
///
/// <para>Diagnostic only, and null in the app. Nothing here is read by any rule.</para>
/// </remarks>
internal sealed class GroupingTrace
{
    /// <param name="SourceIds">The detected blocks this line was built from, in the order they were joined.</param>
    internal sealed record Line(string Id, OcrTextBlock Block, IReadOnlyList<string> SourceIds);

    private readonly Dictionary<object, string> _blockIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, string> _lineIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<(OcrTextBlock Line, IReadOnlyList<string> Sources)> _pendingLines = [];

    /// <summary>The blocks as the engine read them, before any same-line merging.</summary>
    public List<Line> Blocks { get; } = [];

    /// <summary>The lines the next-line rules were actually asked about.</summary>
    public List<Line> Lines { get; } = [];

    /// <summary>Each final group as the ids of the lines in it.</summary>
    public List<IReadOnlyList<string>> Groups { get; } = [];

    public void RegisterBlocks(IReadOnlyList<OcrTextBlock> blocks)
    {
        foreach (var block in blocks)
        {
            var id = $"b{Blocks.Count}";
            _blockIds[block] = id;
            Blocks.Add(new Line(id, block, []));
        }
    }

    /// <summary>
    /// A line as the same-line pass finished building it. Numbered later, by
    /// <see cref="OrderLines"/>: the merge walks the picture's rows in the order it happened to
    /// build them, and ids handed out there run down the page in no particular order.
    /// </summary>
    public void RegisterLine(OcrTextBlock line, IReadOnlyList<OcrTextBlock> sources) =>
        _pendingLines.Add((line, sources.Select(BlockId).ToList()));

    /// <summary>Numbers the lines in the order the next-line rules are about to see them.</summary>
    public void OrderLines(IReadOnlyList<OcrTextBlock> order)
    {
        foreach (var line in order)
        {
            var pending = _pendingLines.FirstOrDefault(candidate => ReferenceEquals(candidate.Line, line));
            var id = $"L{Lines.Count}";
            _lineIds[line] = id;
            Lines.Add(new Line(id, line, pending.Sources ?? []));
        }
    }

    public void RegisterGroups(IReadOnlyList<IReadOnlyList<OcrTextBlock>> groups)
    {
        foreach (var group in groups)
            Groups.Add(group.Select(LineId).ToList());
    }

    /// <summary>The id of a detected block, or "?" for one this pass never saw.</summary>
    public string BlockId(OcrTextBlock block) =>
        _blockIds.TryGetValue(block, out var id) ? id : "?";

    /// <inheritdoc cref="BlockId"/>
    public string LineId(OcrTextBlock line) =>
        _lineIds.TryGetValue(line, out var id) ? id : "?";
}

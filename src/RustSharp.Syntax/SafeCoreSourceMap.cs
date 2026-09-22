using System.Diagnostics;

namespace RustSharp.Syntax;

/// <summary>One UTF-8 source file. Text excludes an optional BOM; Bytes retains the exact file bytes.</summary>
public sealed record SafeCoreSourceDocument(string Path, string Text, ReadOnlyMemory<byte> Bytes);

/// <summary>A location in an original source document, using UTF-16 character offsets.</summary>
public sealed record SafeCoreSourceLocation(SafeCoreSourceDocument Document, TextSpan Span);

/// <summary>One contiguous expanded-source region and its original source range.</summary>
public sealed record SafeCoreSourceMapSegment(
    TextSpan ExpandedSpan, SafeCoreSourceDocument Document, TextSpan SourceSpan, bool IsSynthetic = false);

/// <summary>Maps expanded module source back to the files supplied to the workspace loader.</summary>
public sealed class SafeCoreSourceMap
{
    private readonly SafeCoreSourceMapSegment[] _segments;
    private readonly int _expandedLength;

    public SafeCoreSourceMap(
        IReadOnlyList<SafeCoreSourceDocument> documents,
        IReadOnlyList<SafeCoreSourceMapSegment> segments,
        int expandedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfNegative(expandedLength);
        if (documents.Count is < 1 or > 1024 || segments.Count > 2_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(documents));
        }

        long startedAt = Stopwatch.GetTimestamp();
        var documentSet = new HashSet<SafeCoreSourceDocument>(ReferenceEqualityComparer.Instance);
        var documentArray = new SafeCoreSourceDocument[documents.Count];
        for (int index = 0; index < documentArray.Length; index++)
        {
            CheckConstructionBudget(startedAt, cancellationToken);
            SafeCoreSourceDocument document = documents[index];
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(document.Path);
            ArgumentNullException.ThrowIfNull(document.Text);
            documentArray[index] = document;
            documentSet.Add(document);
        }

        _segments = new SafeCoreSourceMapSegment[segments.Count];
        int expectedStart = 0;
        for (int index = 0; index < _segments.Length; index++)
        {
            CheckConstructionBudget(startedAt, cancellationToken);
            SafeCoreSourceMapSegment segment = segments[index];
            ArgumentNullException.ThrowIfNull(segment);
            if (!documentSet.Contains(segment.Document) || segment.ExpandedSpan.Start != expectedStart ||
                segment.ExpandedSpan.Length <= 0 || segment.ExpandedSpan.Length > expandedLength - expectedStart ||
                segment.SourceSpan.Start < 0 || segment.SourceSpan.Length < 0 ||
                segment.SourceSpan.Start > segment.Document.Text.Length - segment.SourceSpan.Length ||
                !segment.IsSynthetic && segment.ExpandedSpan.Length != segment.SourceSpan.Length)
            {
                throw new ArgumentException("Source-map segments must cover the expanded source in order and refer to valid original ranges.", nameof(segments));
            }
            _segments[index] = segment;
            expectedStart += segment.ExpandedSpan.Length;
        }
        if (expectedStart != expandedLength)
        {
            throw new ArgumentException("Source-map segments must cover the entire expanded source.", nameof(segments));
        }

        Documents = Array.AsReadOnly(documentArray);
        _expandedLength = expandedLength;
    }

    public IReadOnlyList<SafeCoreSourceDocument> Documents { get; }
    /// <summary>Immutable regions for composing module and package source maps.</summary>
    public IReadOnlyList<SafeCoreSourceMapSegment> Segments => Array.AsReadOnly(_segments);

    private static void CheckConstructionBudget(long startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(10))
        {
            throw new TimeoutException("Source-map validation exceeded its time budget.");
        }
    }

    /// <summary>
    /// Maps a span's start precisely. A span crossing an inserted module boundary is clipped
    /// to its first original fragment; inserted delimiters map to the declaration's semicolon.
    /// </summary>
    public SafeCoreSourceLocation MapSpan(TextSpan expanded)
    {
        if (expanded.Start < 0 || expanded.Length < 0 || expanded.Start > _expandedLength - expanded.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(expanded));
        }

        if (_segments.Length == 0)
        {
            return new SafeCoreSourceLocation(Documents[0], new TextSpan(0, 0));
        }

        int lower = 0;
        int upper = _segments.Length - 1;
        // At most 22 comparisons for the bounded two-million-segment input.
        for (int step = 0; step < 32 && lower < upper; step++)
        {
            int middle = lower + (upper - lower) / 2;
            if (_segments[middle].ExpandedSpan.End <= expanded.Start) lower = middle + 1;
            else upper = middle;
        }

        SafeCoreSourceMapSegment segment = _segments[lower];
        int offset = Math.Clamp(expanded.Start - segment.ExpandedSpan.Start, 0, segment.ExpandedSpan.Length);
        int originalStart = segment.SourceSpan.Start + (segment.IsSynthetic ? 0 : offset);
        originalStart = Math.Clamp(originalStart, 0, segment.Document.Text.Length);
        int length = segment.IsSynthetic
            ? Math.Min(expanded.Length, segment.SourceSpan.Length)
            : Math.Min(expanded.Length, Math.Max(0, segment.ExpandedSpan.Length - offset));
        length = Math.Min(length, segment.Document.Text.Length - originalStart);
        return new SafeCoreSourceLocation(segment.Document, new TextSpan(originalStart, length));
    }
}

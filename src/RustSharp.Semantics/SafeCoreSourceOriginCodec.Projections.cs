using System.Text;
using System.Text.Json;

namespace RustSharp.Semantics;

public static partial class SafeCoreSourceOriginCodec
{
    /// <summary>Formats one bounded projection tuple used by ordered enum-value paths.</summary>
    public static string FormatProjection(SafeCoreMirProjection projection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var budget = new OriginBudget(cancellationToken);
        Validate(new(-1, false, [projection], false) { IsStatic = true }, budget);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((int)projection.Kind);
            if (projection.Name is null) writer.WriteNullValue(); else writer.WriteStringValue(projection.Name);
            writer.WriteNumberValue(projection.Index);
            writer.WriteNumberValue(projection.MinimumLength);
            writer.WriteEndArray();
        }
        budget.Check();
        string result = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        if (result.Length > MaximumCharacters) throw Invalid("Projection character budget exceeded.");
        return result;
    }

    /// <summary>Parses one exact canonical projection tuple without accepting unknown fields.</summary>
    public static SafeCoreMirProjection ParseProjection(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > MaximumCharacters) throw Invalid("Projection character budget exceeded.");
        using JsonDocument document = JsonDocument.Parse("[" + text + "]", new JsonDocumentOptions { MaxDepth = 3 });
        SafeCoreMirProjection[] path = ReadPath(document.RootElement, new OriginBudget(cancellationToken));
        if (path.Length != 1 || FormatProjection(path[0], cancellationToken) != text)
            throw Invalid("Projection encoding is not canonical.");
        return path[0];
    }
}

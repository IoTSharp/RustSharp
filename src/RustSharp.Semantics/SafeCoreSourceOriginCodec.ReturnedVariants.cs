using System.Text;
using System.Text.Json;

namespace RustSharp.Semantics;

public static partial class SafeCoreSourceOriginCodec
{
    /// <summary>Formats one exact possible returned enum variant for independent MIR snapshot evidence.</summary>
    public static string FormatReturnedVariant(IReadOnlyList<SafeCoreMirProjection> path, string variantName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantName);
        var budget = new OriginBudget(cancellationToken);
        budget.Check();
        if (path.Count > MaximumDepth || variantName.Length > MaximumCharacters || variantName.Contains('\0'))
            throw Invalid("Returned enum variant identity or path exceeds its budget.");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("valuePath");
            foreach (SafeCoreMirProjection projection in path)
            {
                budget.Step();
                ArgumentNullException.ThrowIfNull(projection);
                if (projection.Kind is not (SafeCoreMirProjectionKind.Field or SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex))
                    throw Invalid("Returned enum variants require exact aggregate value paths.");
                writer.WriteStringValue(FormatProjection(projection, cancellationToken));
            }
            writer.WriteEndArray();
            writer.WriteString("variantName", variantName);
            writer.WriteEndObject();
        }
        budget.Check();
        string result = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        if (result.Length > MaximumCharacters) throw Invalid("Returned enum variant character budget exceeded.");
        return result;
    }
}

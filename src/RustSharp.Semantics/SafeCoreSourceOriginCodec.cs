using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RustSharp.Semantics;

/// <summary>Lossless, versioned source-package reference origins and projection paths.</summary>
public static partial class SafeCoreSourceOriginCodec
{
    public const string Prefix = "rustsharp-origin-v1:";
    public const int MaximumCharacters = 4096;
    public const int MaximumDepth = 32;
    public const int MaximumItems = 256;

    public static string Format(SafeCoreMirReferenceOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var budget = new OriginBudget(cancellationToken);
        Validate(origin, budget);
        if (origin.ValuePath.Count == 0 && origin.ParameterPath.Count == 0 && origin.Projections.Count == 0 &&
            !origin.IsMutable && !origin.HasUnknownSliceOffset)
        {
            if (origin.IsParameter && !origin.IsStatic)
                return "parameter:" + origin.LocalId.ToString(CultureInfo.InvariantCulture);
            if (origin.IsStatic && !origin.IsParameter && origin.LocalId == -1) return "static";
        }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("localId", origin.LocalId);
            writer.WriteBoolean("parameter", origin.IsParameter);
            writer.WriteBoolean("static", origin.IsStatic);
            writer.WriteBoolean("mutable", origin.IsMutable);
            writer.WriteBoolean("unknownSliceOffset", origin.HasUnknownSliceOffset);
            WritePath(writer, "valuePath", origin.ValuePath, budget);
            WritePath(writer, "parameterPath", origin.ParameterPath, budget);
            WritePath(writer, "projections", origin.Projections, budget);
            writer.WriteEndObject();
        }
        string text = Prefix + Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        budget.Check();
        if (text.Length > MaximumCharacters) throw Invalid("Origin character budget exceeded.");
        return text;
    }

    public static SafeCoreMirReferenceOrigin Parse(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var budget = new OriginBudget(cancellationToken);
        budget.Check();
        if (text.Length > MaximumCharacters) throw Invalid("Origin character budget exceeded.");
        if (text == "static") return new(-1, false, [], false) { IsStatic = true };
        const string parameterPrefix = "parameter:";
        if (text.StartsWith(parameterPrefix, StringComparison.Ordinal))
        {
            if (!int.TryParse(text.AsSpan(parameterPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out int index) || index is < 0 or >= MaximumItems ||
                text != parameterPrefix + index.ToString(CultureInfo.InvariantCulture)) throw Invalid("Invalid root parameter origin.");
            return new(index, true, [], false);
        }
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw Invalid("Unknown source origin schema.");
        using JsonDocument json = JsonDocument.Parse(text[Prefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("Origin must be an object.");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            budget.Step();
            if (!properties.Add(property.Name) || property.Name is not ("localId" or "parameter" or "static" or
                "mutable" or "unknownSliceOffset" or "valuePath" or "parameterPath" or "projections"))
                throw Invalid("Origin contains duplicate or unknown properties.");
        }
        if (properties.Count != 8) throw Invalid("Origin fields are incomplete.");
        if (!root.GetProperty("localId").TryGetInt32(out int localId)) throw Invalid("Invalid origin local ID.");
        var result = new SafeCoreMirReferenceOrigin(localId, Boolean(root, "parameter"),
            ReadPath(root.GetProperty("projections"), budget), Boolean(root, "mutable"))
        {
            IsStatic = Boolean(root, "static"),
            HasUnknownSliceOffset = Boolean(root, "unknownSliceOffset"),
            ValuePath = ReadPath(root.GetProperty("valuePath"), budget),
            ParameterPath = ReadPath(root.GetProperty("parameterPath"), budget),
        };
        Validate(result, budget);
        if (Format(result, cancellationToken) != text) throw Invalid("Origin encoding is not canonical.");
        return result;
    }

    private static bool Boolean(JsonElement root, string name)
    {
        JsonElement property = root.GetProperty(name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("Origin boolean fields must be explicit booleans."),
        };
    }

    private static void Validate(SafeCoreMirReferenceOrigin origin, OriginBudget budget)
    {
        budget.Check();
        if (origin.LocalId is < -1 or >= MaximumItems || !origin.IsParameter && !origin.IsStatic ||
            origin.IsParameter && origin.LocalId < 0 || origin.IsStatic && origin.IsMutable && !origin.IsParameter)
            throw Invalid("Origin must be rooted in a parameter or immutable static storage.");
        foreach (IReadOnlyList<SafeCoreMirProjection> path in new[] { origin.ValuePath, origin.ParameterPath, origin.Projections })
        {
            if (path is null || path.Count > MaximumDepth) throw Invalid("Origin projection depth exceeded.");
            foreach (SafeCoreMirProjection projection in path)
            {
                budget.Step();
                if (projection is null || !Enum.IsDefined(projection.Kind) ||
                    projection.Kind != SafeCoreMirProjectionKind.FromEndIndex && projection.MinimumLength != 0)
                    throw Invalid("Invalid origin projection kind or bound.");
                bool valid = projection.Kind switch
                {
                    SafeCoreMirProjectionKind.Field => !string.IsNullOrWhiteSpace(projection.Name) &&
                        projection.Name.Length <= MaximumCharacters && !projection.Name.Contains('\0') && projection.Index == -1,
                    SafeCoreMirProjectionKind.Dereference => projection.Name is null && projection.Index == -1,
                    SafeCoreMirProjectionKind.FromEndIndex => projection.Name is null && projection.Index > 0 &&
                        projection.MinimumLength >= projection.Index,
                    _ => projection.Name is null && projection.Index >= 0,
                };
                if (!valid) throw Invalid("Invalid origin projection fields.");
            }
        }
    }

    private static void WritePath(Utf8JsonWriter writer, string name, IReadOnlyList<SafeCoreMirProjection> path, OriginBudget budget)
    {
        writer.WriteStartArray(name);
        foreach (SafeCoreMirProjection projection in path)
        {
            budget.Step();
            writer.WriteStartArray();
            writer.WriteNumberValue((int)projection.Kind);
            if (projection.Name is null) writer.WriteNullValue(); else writer.WriteStringValue(projection.Name);
            writer.WriteNumberValue(projection.Index);
            writer.WriteNumberValue(projection.MinimumLength);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static SafeCoreMirProjection[] ReadPath(JsonElement array, OriginBudget budget)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumDepth)
            throw Invalid("Invalid origin projection path.");
        var result = new List<SafeCoreMirProjection>(array.GetArrayLength());
        foreach (JsonElement projection in array.EnumerateArray())
        {
            budget.Step();
            if (projection.ValueKind != JsonValueKind.Array || projection.GetArrayLength() != 4 ||
                !projection[0].TryGetInt32(out int kind) || !projection[2].TryGetInt32(out int index) ||
                !projection[3].TryGetInt32(out int minimumLength) ||
                projection[1].ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw Invalid("Invalid origin projection tuple.");
            result.Add(new((SafeCoreMirProjectionKind)kind, projection[1].GetString(), index) { MinimumLength = minimumLength });
        }
        return result.ToArray();
    }

    private sealed class OriginBudget(CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _items;
        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.Elapsed > TimeSpan.FromSeconds(5)) throw Invalid("Origin codec time budget exceeded.");
        }
        public void Step()
        {
            Check();
            if (++_items > MaximumItems) throw Invalid("Origin item budget exceeded.");
        }
    }

    private static ArgumentException Invalid(string reason) => new(reason);
}

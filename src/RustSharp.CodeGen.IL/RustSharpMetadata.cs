using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

/// <summary>A deterministic function signature carried in Rust# assembly metadata.</summary>
public sealed record RustSharpMetadataFunction(string Name, string Signature);

/// <summary>A closed generic instance carried in Rust# assembly metadata.</summary>
public sealed record RustSharpMetadataGenericInstance(string FunctionId, string Arguments);

/// <summary>
/// Versioned language metadata that is embedded in generated assemblies. CLR
/// metadata cannot express Rust ownership, profile or monomorphization facts;
/// this bounded document is the consumer-facing contract for those facts.
/// </summary>
public sealed partial record RustSharpMetadataDocument
{
    public const string SchemaVersion = "rustsharp-metadata-v1";
    public const int MaximumFunctions = 4096;
    public const int MaximumGenericInstances = 4096;
    public const int MaximumTraits = 4096;
    public const int MaximumJsonCharacters = 1_000_000;

    public RustSharpMetadataDocument(
        string profile,
        string sourceSha256,
        IEnumerable<RustSharpMetadataFunction>? functions = null,
        IEnumerable<RustSharpMetadataGenericInstance>? genericInstances = null,
        IEnumerable<string>? traitImplementations = null,
        string? mirSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        if (profile.Length > 256 || sourceSha256.Length != 64 ||
            sourceSha256.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Rust# metadata identity is invalid.");

        Schema = SchemaVersion;
        Profile = profile;
        SourceSha256 = sourceSha256;
        Functions = NormalizeFunctions(functions);
        GenericInstances = NormalizeGenericInstances(genericInstances);
        TraitImplementations = NormalizeStrings(traitImplementations, MaximumTraits, "trait implementations");
        MirSnapshot = mirSnapshot is null ? null : LimitText(mirSnapshot, MaximumJsonCharacters);
        Json = Serialize(this);
        if (Json.Length > MaximumJsonCharacters)
            throw new ArgumentException("Rust# metadata JSON exceeds its size limit.");
    }

    public string Schema { get; }
    public string Profile { get; }
    public string SourceSha256 { get; }
    public ImmutableArray<RustSharpMetadataFunction> Functions { get; }
    public ImmutableArray<RustSharpMetadataGenericInstance> GenericInstances { get; }
    public ImmutableArray<string> TraitImplementations { get; }
    public string? MirSnapshot { get; }
    [JsonIgnore]
    public string Json { get; }

    public static RustSharpMetadataDocument ForProgram(
        string profile,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<ClrLirMethod> methods,
        IEnumerable<RustSharpMetadataGenericInstance>? genericInstances = null,
        IEnumerable<string>? traitImplementations = null,
        string? mirSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count > MaximumFunctions) throw new ArgumentException("Too many methods.", nameof(methods));
        var functions = new RustSharpMetadataFunction[methods.Count];
        for (int index = 0; index < methods.Count; index++)
        {
            ClrLirMethod method = methods[index] ?? throw new ArgumentException("Method cannot be null.", nameof(methods));
            string signature = string.Join(",", method.Parameters.Select(static type => type.ToString())) +
                "->" + method.ReturnType;
            functions[index] = new(method.Name, signature);
        }

        return new(profile, Convert.ToHexString(SHA256.HashData(sourceBytes)), functions,
            genericInstances, traitImplementations, mirSnapshot);
    }

    private static ImmutableArray<RustSharpMetadataFunction> NormalizeFunctions(IEnumerable<RustSharpMetadataFunction>? values)
    {
        if (values is null) return [];
        RustSharpMetadataFunction[] result = Materialize(values, MaximumFunctions, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# metadata function count is out of bounds.", nameof(values));
        foreach (RustSharpMetadataFunction value in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Signature);
        }

        return [.. result.OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Signature, StringComparer.Ordinal)];
    }

    private static ImmutableArray<RustSharpMetadataGenericInstance> NormalizeGenericInstances(IEnumerable<RustSharpMetadataGenericInstance>? values)
    {
        if (values is null) return [];
        RustSharpMetadataGenericInstance[] result = Materialize(values, MaximumGenericInstances, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# generic metadata count is out of bounds.", nameof(values));
        foreach (RustSharpMetadataGenericInstance value in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.FunctionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Arguments);
        }

        return [.. result.OrderBy(static value => value.FunctionId, StringComparer.Ordinal)
            .ThenBy(static value => value.Arguments, StringComparer.Ordinal)];
    }

    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string>? values, int maximum, string label)
    {
        if (values is null) return [];
        string[] result = Materialize(values, maximum, nameof(values));
        if (result.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Rust# metadata {label} are invalid.", nameof(values));
        return [.. result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static T[] Materialize<T>(IEnumerable<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<T>(Math.Min(maximum, 16));
        using IEnumerator<T> enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (result.Count >= maximum)
                throw new ArgumentException("Rust# metadata collection exceeds its bound.", parameterName);
            result.Add(enumerator.Current);
        }

        return result.ToArray();
    }

    private static string Serialize(RustSharpMetadataDocument document) =>
        JsonSerializer.Serialize(document, MetadataJsonContext.Default.RustSharpMetadataDocument);

    private static string LimitText(string value, int maximum) =>
        value.Length <= maximum ? value : throw new ArgumentException("Rust# metadata text exceeds its size limit.");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(RustSharpMetadataDocument))]
    private sealed partial class MetadataJsonContext : JsonSerializerContext;
}

/// <summary>Reads the embedded AssemblyMetadataAttribute without loading generated code.</summary>
public static class RustSharpMetadataReader
{
    public const string AttributeKey = "RustSharp.Metadata.v1";

    public static string? FindJson(System.Reflection.Metadata.MetadataReader metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (System.Reflection.Metadata.CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            System.Reflection.Metadata.CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            string? name = TryGetAttributeTypeName(metadata, attribute.Constructor);
            if (!string.Equals(name, "System.Reflection.AssemblyMetadataAttribute", StringComparison.Ordinal)) continue;
            string? value = TryDecodeValue(metadata, attribute.Value);
            if (value is not null && value.StartsWith(AttributeKey + "\u001f", StringComparison.Ordinal))
                return value[(AttributeKey.Length + 1)..];
        }

        return null;
    }

    private static string? TryGetAttributeTypeName(System.Reflection.Metadata.MetadataReader metadata,
        System.Reflection.Metadata.EntityHandle constructor)
    {
        System.Reflection.Metadata.EntityHandle parent = constructor.Kind switch
        {
            System.Reflection.Metadata.HandleKind.MemberReference => metadata.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)constructor).Parent,
            System.Reflection.Metadata.HandleKind.MethodDefinition => ((System.Reflection.Metadata.EntityHandle)constructor),
            _ => default,
        };
        if (parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) return null;
        System.Reflection.Metadata.TypeReference reference = metadata.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)parent);
        return metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
    }

    private static string? TryDecodeValue(System.Reflection.Metadata.MetadataReader metadata,
        System.Reflection.Metadata.BlobHandle value)
    {
        try
        {
            System.Reflection.Metadata.BlobReader reader = metadata.GetBlobReader(value);
            if (reader.ReadUInt16() != 1) return null;
            string? key = reader.ReadSerializedString();
            string? json = reader.ReadSerializedString();
            return key is null || json is null ? null : key + "\u001f" + json;
        }
        catch (Exception exception) when (exception is BadImageFormatException or
            ArgumentException or InvalidOperationException)
        {
            // Malformed custom-attribute blobs are untrusted PE input. Treat
            // them as an absent Rust# attribute instead of leaking a parser
            // exception to a consumer inspecting an otherwise readable PE.
            return null;
        }
    }
}

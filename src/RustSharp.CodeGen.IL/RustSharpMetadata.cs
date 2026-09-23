using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// A deterministic function export carried in Rust# assembly metadata.  <see
/// cref="Name"/> is the emitted CLR method name retained for v1 compatibility;
/// <see cref="SourceQualifiedName"/> is the source/HIR identity used to link
/// ownership evidence, and <see cref="IsPublic"/> controls cross-package use.
/// </summary>
public sealed record RustSharpMetadataFunction(
    string Name,
    string Signature,
    string? SourceQualifiedName = null,
    bool IsPublic = true);

/// <summary>A closed generic instance carried in Rust# assembly metadata.</summary>
public sealed record RustSharpMetadataGenericInstance(string FunctionId, string Arguments);

/// <summary>
/// Ownership evidence persisted for one emitted function.  The backend never
/// infers safety from CLR value semantics; consumers can use these facts to
/// decide whether a producer was checked with the declared ownership pass.
/// </summary>
public sealed record RustSharpMetadataOwnershipFunction(
    string FunctionId,
    string PanicStrategy,
    IEnumerable<string>? DropOrder = null,
    IEnumerable<string>? Outcomes = null,
    IEnumerable<string>? BorrowFacts = null);

/// <summary>A bounded result returned when an independent assembly is imported.</summary>
public sealed record RustSharpMetadataImportResult(
    string AssemblyPath,
    RustSharpMetadataDocument? Document,
    string? GenericMetadataJson,
    Guid? ModuleVersionId,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The CLR AssemblyDef name, which may differ from the file name.</summary>
    public string? AssemblyName { get; init; }

    public bool IsSuccessful => Document is not null && Diagnostics.Count == 0;
}

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
    public const int MaximumOwnershipFunctions = 4096;
    public const int MaximumOwnershipFactsPerFunction = 4096;
    public const int MaximumJsonCharacters = 1_000_000;

    public RustSharpMetadataDocument(
        string profile,
        string sourceSha256,
        IEnumerable<RustSharpMetadataFunction>? functions = null,
        IEnumerable<RustSharpMetadataGenericInstance>? genericInstances = null,
        IEnumerable<string>? traitImplementations = null,
        string? mirSnapshot = null,
        IEnumerable<RustSharpMetadataOwnershipFunction>? ownership = null,
        string? cleanupSnapshot = null)
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
        Ownership = NormalizeOwnership(ownership);
        CleanupSnapshot = cleanupSnapshot is null ? null : LimitText(cleanupSnapshot, MaximumJsonCharacters);
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
    public ImmutableArray<RustSharpMetadataOwnershipFunction> Ownership { get; }
    /// <summary>Optional deterministic P1-08 cleanup projection snapshot.</summary>
    public string? CleanupSnapshot { get; }
    [JsonIgnore]
    public string Json { get; }

    public static RustSharpMetadataDocument ForProgram(
        string profile,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<ClrLirMethod> methods,
        IEnumerable<RustSharpMetadataGenericInstance>? genericInstances = null,
        IEnumerable<string>? traitImplementations = null,
        string? mirSnapshot = null,
        IEnumerable<RustSharpMetadataOwnershipFunction>? ownership = null,
        string? cleanupSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count > MaximumFunctions) throw new ArgumentException("Too many methods.", nameof(methods));
        var functions = new RustSharpMetadataFunction[methods.Count];
        for (int index = 0; index < methods.Count; index++)
        {
            ClrLirMethod method = methods[index] ?? throw new ArgumentException("Method cannot be null.", nameof(methods));
            string signature = string.Join(",", method.Parameters.Select(static type => type.ToString())) +
                "->" + method.ReturnType;
            functions[index] = new(
                method.Name,
                signature,
                method.SourceQualifiedName ?? method.Name,
                method.IsPublic);
        }

        return new(profile, Convert.ToHexString(SHA256.HashData(sourceBytes)), functions,
            genericInstances, traitImplementations, mirSnapshot, ownership, cleanupSnapshot);
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
            if (value.Name.Length > 4096 || value.Signature.Length > 4096)
                throw new ArgumentException("Rust# metadata function identity is too long.", nameof(values));
            if (value.SourceQualifiedName is not null &&
                (string.IsNullOrWhiteSpace(value.SourceQualifiedName) || value.SourceQualifiedName.Length > 4096))
                throw new ArgumentException("Rust# metadata source function identity is invalid.", nameof(values));
        }

        return [.. result.OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Signature, StringComparer.Ordinal)
            .ThenBy(static value => value.SourceQualifiedName, StringComparer.Ordinal)];
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

    private static ImmutableArray<RustSharpMetadataOwnershipFunction> NormalizeOwnership(
        IEnumerable<RustSharpMetadataOwnershipFunction>? values)
    {
        if (values is null) return [];
        RustSharpMetadataOwnershipFunction[] result = Materialize(
            values, MaximumOwnershipFunctions, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# ownership metadata count is out of bounds.", nameof(values));

        var normalized = new List<RustSharpMetadataOwnershipFunction>(result.Length);
        foreach (RustSharpMetadataOwnershipFunction value in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.FunctionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.PanicStrategy);
            if (value.FunctionId.Length > 4096 || value.PanicStrategy.Length > 64)
                throw new ArgumentException("Rust# ownership metadata identity is too long.", nameof(values));

            string[] drops = NormalizeFacts(value.DropOrder, "drop order");
            string[] outcomes = NormalizeFacts(value.Outcomes, "outcomes");
            string[] borrows = NormalizeFacts(value.BorrowFacts, "borrow facts");
            normalized.Add(new(value.FunctionId, value.PanicStrategy, drops, outcomes, borrows));
        }

        return [.. normalized
            .OrderBy(static value => value.FunctionId, StringComparer.Ordinal)
            .ThenBy(static value => value.PanicStrategy, StringComparer.Ordinal)
            .ThenBy(static value => string.Join("\u001f", value.DropOrder ?? []), StringComparer.Ordinal)
            .ThenBy(static value => string.Join("\u001f", value.Outcomes ?? []), StringComparer.Ordinal)
            .ThenBy(static value => string.Join("\u001f", value.BorrowFacts ?? []), StringComparer.Ordinal)];

        static string[] NormalizeFacts(IEnumerable<string>? facts, string label)
        {
            if (facts is null) return [];
            var result = new List<string>();
            using IEnumerator<string> enumerator = facts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (result.Count >= MaximumOwnershipFactsPerFunction)
                    throw new ArgumentException($"Rust# ownership {label} exceed their bound.", nameof(values));
                string fact = enumerator.Current ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fact) || fact.Length > 4096)
                    throw new ArgumentException($"Rust# ownership {label} are invalid.", nameof(values));
                result.Add(fact);
            }

            return [.. result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
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
    [JsonSerializable(typeof(MetadataPayload))]
    private sealed partial class MetadataJsonContext : JsonSerializerContext;

    /// <summary>Parses and canonicalizes a persisted metadata document.</summary>
    public static RustSharpMetadataDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonCharacters)
            throw new ArgumentException("Rust# metadata JSON exceeds its size limit.", nameof(json));

        RustSharpMetadataReader.ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 128,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Rust# metadata root must be an object.", nameof(json));

        MetadataPayload? parsed = JsonSerializer.Deserialize(
            json, MetadataJsonContext.Default.MetadataPayload);
        if (parsed is null)
            throw new ArgumentException("Rust# metadata document is empty.", nameof(json));
        if (!string.Equals(parsed.Schema, SchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported Rust# metadata schema.", nameof(json));
        if (parsed.Profile is null || parsed.SourceSha256 is null)
            throw new ArgumentException("Rust# metadata identity is incomplete.", nameof(json));

        // Reconstruct through the validating constructor. The document has
        // derived Schema/Json state and is intentionally not a direct
        // deserialization target.
        var canonical = new RustSharpMetadataDocument(
            parsed.Profile,
            parsed.SourceSha256,
            parsed.Functions,
            parsed.GenericInstances,
            parsed.TraitImplementations,
            parsed.MirSnapshot,
            parsed.Ownership,
            parsed.CleanupSnapshot);
        // Older v1 producers predate the ownership extension and therefore do
        // not contain an `ownership` property. Return the canonical document
        // while accepting that additive shape for cross-package compatibility.
        return canonical;
    }

    private sealed class MetadataPayload
    {
        public string? Schema { get; set; }
        public string? Profile { get; set; }
        public string? SourceSha256 { get; set; }
        public RustSharpMetadataFunction[]? Functions { get; set; }
        public RustSharpMetadataGenericInstance[]? GenericInstances { get; set; }
        public string[]? TraitImplementations { get; set; }
        public string? MirSnapshot { get; set; }
        public RustSharpMetadataOwnershipFunction[]? Ownership { get; set; }
        public string? CleanupSnapshot { get; set; }
    }
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

    /// <summary>Reads and validates the canonical Rust# metadata document.</summary>
    public static RustSharpMetadataDocument? ReadDocument(System.Reflection.Metadata.MetadataReader metadata)
    {
        string? json = FindJson(metadata);
        return json is null ? null : RustSharpMetadataDocument.Parse(json);
    }

    /// <summary>Reads the metadata attribute from a PE without loading its assembly.</summary>
    public static RustSharpMetadataDocument ReadAssembly(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("The assembly does not contain CLR metadata.");
        return ReadDocument(pe.GetMetadataReader()) ??
            throw new InvalidDataException("The assembly has no Rust# metadata attribute.");
    }

    internal static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
        var objects = new Stack<HashSet<string>>();
        int tokenCount = 0;
        while (reader.Read())
        {
            if (++tokenCount > 16_384)
                throw new JsonException("Rust# metadata exceeds its token limit.");
            if (reader.TokenType == JsonTokenType.StartObject)
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? string.Empty))
                    throw new JsonException("Rust# metadata contains a duplicate property.");
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (objects.Count == 0) throw new JsonException("Rust# metadata object structure is invalid.");
                objects.Pop();
            }
        }

        if (objects.Count != 0)
            throw new JsonException("Rust# metadata object is incomplete.");
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

/// <summary>
/// Imports Rust# metadata from an independently built assembly. This is a
/// read-only contract: it validates profile/schema/linkage and never executes
/// producer code or uses reflection-based discovery.
/// </summary>
public static class RustSharpMetadataConsumer
{
    public const string MissingMetadata = "RSC0010";
    public const string InvalidMetadata = "RSC0011";
    public const string ProfileMismatch = "RSC0012";
    public const string ExportMissing = "RSC0013";
    public const int MaximumAssemblyBytes = 256 * 1024 * 1024;

    public static RustSharpMetadataImportResult ReadAssembly(
        string assemblyPath,
        string? expectedProfile = null,
        IEnumerable<string>? requiredFunctions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);
        var diagnostics = new List<string>();
        RustSharpMetadataDocument? document = null;
        string? generic = null;
        Guid? mvid = null;
        string? assemblyName = null;
        try
        {
            FileInfo info = new(fullPath);
            if (!info.Exists) throw new FileNotFoundException("Rust# producer assembly was not found.", fullPath);
            if (info.Length <= 0 || info.Length > MaximumAssemblyBytes)
                throw new InvalidDataException("Rust# producer assembly exceeds its bounded size.");
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw new BadImageFormatException("Producer assembly has no CLR metadata.");
            MetadataReader metadata = pe.GetMetadataReader();
            assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            document = RustSharpMetadataReader.ReadDocument(metadata);
            if (document is null)
            {
                diagnostics.Add(MissingMetadata + ": producer assembly has no Rust# metadata attribute.");
            }
            else
            {
                if (expectedProfile is not null && !string.Equals(document.Profile, expectedProfile, StringComparison.Ordinal))
                    diagnostics.Add(ProfileMismatch + ": producer profile does not match the consumer profile.");
                ValidateGeneratedFunctions(metadata, document, diagnostics);
                ValidateRequiredFunctions(document, requiredFunctions, diagnostics);
            }

            ModuleDefinition module = metadata.GetModuleDefinition();
            mvid = metadata.GetGuid(module.Mvid);
            generic = TryReadGenericResource(pe, metadata, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            BadImageFormatException or InvalidDataException or ArgumentException or JsonException or
            EndOfStreamException or OverflowException or InvalidOperationException or NotSupportedException)
        {
            diagnostics.Add(InvalidMetadata + ": " + Trim(exception.Message));
        }

        return new(fullPath, document, generic, mvid, diagnostics.AsReadOnly())
        {
            AssemblyName = assemblyName,
        };
    }

    public static RustSharpMetadataFunction? FindFunction(
        RustSharpMetadataDocument document, string functionId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionId);
        return document.Functions.FirstOrDefault(function =>
            string.Equals(function.Name, functionId, StringComparison.Ordinal) ||
            string.Equals(function.SourceQualifiedName, functionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Correlates the language-level export list with the generated Program
    /// type in the PE.  The JSON attribute is untrusted input: accepting its
    /// names or signatures without checking the MethodDef table would let a
    /// stale or forged document describe methods that are not actually
    /// callable from the producer assembly.
    /// </summary>
    private static void ValidateGeneratedFunctions(
        MetadataReader metadata,
        RustSharpMetadataDocument document,
        List<string> diagnostics)
    {
        const string generatedNamespace = "RustSharp.Generated";
        const string generatedTypeName = "Program";
        int typeRows = metadata.GetTableRowCount(TableIndex.TypeDef);
        int typeLimit = Math.Min(typeRows, RustSharpMetadataDocument.MaximumFunctions + 1);
        TypeDefinitionHandle programHandle = default;
        int programCount = 0;
        for (int row = 1; row <= typeLimit; row++)
        {
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(row);
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            if (string.Equals(metadata.GetString(definition.Namespace), generatedNamespace, StringComparison.Ordinal) &&
                string.Equals(metadata.GetString(definition.Name), generatedTypeName, StringComparison.Ordinal))
            {
                programHandle = handle;
                programCount++;
            }
        }

        if (typeRows > typeLimit)
        {
            AddContractDiagnostic(diagnostics,
                "generated type table exceeds its bounded correlation limit.");
        }

        if (programCount == 0)
        {
            AddContractDiagnostic(diagnostics,
                "generated RustSharp.Generated.Program type is missing.");
            return;
        }

        if (programCount != 1)
        {
            AddContractDiagnostic(diagnostics,
                "generated RustSharp.Generated.Program type is duplicated.");
            return;
        }

        TypeDefinition program = metadata.GetTypeDefinition(programHandle);
        var actual = new Dictionary<string, GeneratedMethodShape>(StringComparer.Ordinal);
        int methodCount = 0;
        foreach (MethodDefinitionHandle handle in program.GetMethods())
        {
            if (++methodCount > RustSharpMetadataDocument.MaximumFunctions)
            {
                AddContractDiagnostic(diagnostics,
                    "generated method table exceeds its bounded correlation limit.");
                break;
            }

            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);
            if (!TryDecodeGeneratedMethod(metadata, method, out GeneratedMethodShape shape, out string? error))
            {
                AddContractDiagnostic(diagnostics,
                    "generated method '" + Trim(name) + "' has an invalid CLR signature: " + Trim(error));
                continue;
            }

            if (!actual.TryAdd(name, shape))
            {
                AddContractDiagnostic(diagnostics,
                    "generated method name '" + Trim(name) + "' is duplicated.");
            }
        }

        if (methodCount > RustSharpMetadataDocument.MaximumFunctions)
        {
            return;
        }

        if (actual.Count != document.Functions.Length)
        {
            AddContractDiagnostic(diagnostics,
                "metadata function count " + document.Functions.Length.ToString(CultureInfo.InvariantCulture) +
                " does not match generated MethodDef count " + actual.Count.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (RustSharpMetadataFunction function in document.Functions)
        {
            if (!declared.Add(function.Name))
            {
                AddContractDiagnostic(diagnostics,
                    "metadata function name '" + Trim(function.Name) + "' is duplicated.");
                continue;
            }

            if (!actual.TryGetValue(function.Name, out GeneratedMethodShape shape))
            {
                AddContractDiagnostic(diagnostics,
                    "metadata function '" + Trim(function.Name) + "' has no generated MethodDef.");
                continue;
            }

            if (!string.Equals(function.Signature, shape.Signature, StringComparison.Ordinal))
            {
                AddContractDiagnostic(diagnostics,
                    "metadata function '" + Trim(function.Name) + "' signature does not match the generated MethodDef.");
            }

            if (!shape.IsStatic)
            {
                AddContractDiagnostic(diagnostics,
                    "generated method '" + Trim(function.Name) + "' must be static.");
            }

            if (shape.IsPublic != function.IsPublic)
            {
                AddContractDiagnostic(diagnostics,
                    "metadata function '" + Trim(function.Name) + "' visibility does not match the generated MethodDef.");
            }
        }

        foreach (string name in actual.Keys)
        {
            if (!declared.Contains(name))
            {
                AddContractDiagnostic(diagnostics,
                    "generated MethodDef '" + Trim(name) + "' is missing from metadata functions.");
            }
        }
    }

    private static bool TryDecodeGeneratedMethod(
        MetadataReader metadata,
        MethodDefinition method,
        out GeneratedMethodShape shape,
        out string? error)
    {
        shape = default;
        error = null;
        try
        {
            MethodSignature<string> signature = method.DecodeSignature(
                GeneratedSignatureTypeProvider.Instance, genericContext: null);
            if (signature.Header.Kind != SignatureKind.Method ||
                signature.Header.CallingConvention != SignatureCallingConvention.Default ||
                signature.Header.IsInstance || signature.Header.IsGeneric || signature.Header.HasExplicitThis ||
                signature.RequiredParameterCount != signature.ParameterTypes.Length)
            {
                error = "unsupported method calling convention or signature shape";
                return false;
            }

            string canonical = string.Join(",", signature.ParameterTypes) + "->" + signature.ReturnType;
            System.Reflection.MethodAttributes attributes = method.Attributes;
            System.Reflection.MethodAttributes access = attributes &
                System.Reflection.MethodAttributes.MemberAccessMask;
            if (access is not System.Reflection.MethodAttributes.Public and
                not System.Reflection.MethodAttributes.Assembly)
            {
                error = "generated method visibility is neither public nor assembly";
                return false;
            }

            shape = new(
                canonical,
                access == System.Reflection.MethodAttributes.Public,
                (attributes & System.Reflection.MethodAttributes.Static) != 0);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or
            InvalidOperationException or NotSupportedException or IndexOutOfRangeException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void AddContractDiagnostic(List<string> diagnostics, string message)
    {
        // Keep malformed PE diagnostics bounded even when a producer contains
        // thousands of duplicate or mismatched MethodDefs.
        if (diagnostics.Count < 256)
        {
            diagnostics.Add(InvalidMetadata + ": " + Trim(message));
        }
    }

    private readonly record struct GeneratedMethodShape(
        string Signature,
        bool IsPublic,
        bool IsStatic);

    private sealed class GeneratedSignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public static GeneratedSignatureTypeProvider Instance { get; } = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "Void",
            PrimitiveTypeCode.Int32 => "I32",
            PrimitiveTypeCode.Boolean => "Bool",
            PrimitiveTypeCode.String => "Text",
            PrimitiveTypeCode.Object => "Any",
            _ => throw Unsupported("primitive type " + typeCode),
        };

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            if (reader.ResolveSignatureTypeKind(handle, rawTypeKind) != SignatureTypeKind.ValueType)
            {
                throw Unsupported("a generated reference type");
            }

            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string @namespace = reader.GetString(definition.Namespace);
            if (!string.Equals(@namespace, "RustSharp.Generated.Values", StringComparison.Ordinal))
            {
                throw Unsupported("a non-generated value type");
            }

            return "Value(" + reader.GetString(definition.Name) + ")";
        }

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => throw Unsupported("a type reference");

        public string GetSZArrayType(string elementType) => throw Unsupported("an SZ array");

        public string GetArrayType(string elementType, ArrayShape shape) => throw Unsupported("an array");

        public string GetByReferenceType(string elementType) => throw Unsupported("a by-reference type");

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            throw Unsupported("a function pointer");

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            throw Unsupported("a generic type instantiation");

        public string GetGenericMethodParameter(object? genericContext, int index) =>
            throw Unsupported("a generic method parameter");

        public string GetGenericTypeParameter(object? genericContext, int index) =>
            throw Unsupported("a generic type parameter");

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            throw Unsupported("a modified type");

        public string GetPinnedType(string elementType) => throw Unsupported("a pinned type");

        public string GetPointerType(string elementType) => throw Unsupported("a pointer type");

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => throw Unsupported("a type specification");

        private static NotSupportedException Unsupported(string kind) =>
            new("Unsupported generated method signature " + kind + ".");
    }

    private static void ValidateRequiredFunctions(
        RustSharpMetadataDocument document,
        IEnumerable<string>? requiredFunctions,
        List<string> diagnostics)
    {
        if (requiredFunctions is null) return;
        int count = 0;
        foreach (string required in requiredFunctions)
        {
            if (++count > RustSharpMetadataDocument.MaximumFunctions)
            {
                diagnostics.Add(ExportMissing + ": required function list exceeds its bound.");
                return;
            }
            RustSharpMetadataFunction? function = string.IsNullOrWhiteSpace(required)
                ? null
                : FindFunction(document, required);
            if (function is null)
            {
                diagnostics.Add(ExportMissing + ": producer does not export '" + Trim(required) + "'.");
            }
            else if (!function.IsPublic)
            {
                diagnostics.Add(ExportMissing + ": producer export '" + Trim(required) + "' is private.");
            }
        }
    }

    private static string? TryReadGenericResource(
        PEReader pe, MetadataReader metadata, List<string> diagnostics)
    {
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!string.Equals(metadata.GetString(resource.Name), "RustSharp.Generics.v1.json", StringComparison.Ordinal))
                continue;
            if (!resource.Implementation.IsNil)
            {
                diagnostics.Add(InvalidMetadata + ": generic metadata must be embedded in the producer assembly.");
                return null;
            }
            DirectoryEntry directory = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
            if (directory.RelativeVirtualAddress == 0 || resource.Offset < 0)
            {
                diagnostics.Add(InvalidMetadata + ": generic metadata resource directory is missing.");
                return null;
            }
            try
            {
                PEMemoryBlock section = pe.GetSectionData(directory.RelativeVirtualAddress);
                int offset = checked((int)resource.Offset);
                var reader = section.GetReader(offset, section.Length - offset);
                int length = reader.ReadInt32();
                if (length < 0 || length > SafeCoreMetadataLimit || length > reader.RemainingBytes)
                    throw new InvalidDataException("Generic metadata resource exceeds its bound.");
                byte[] bytes = reader.ReadBytes(length);
                using JsonDocument _ = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                BadImageFormatException or EndOfStreamException)
            {
                diagnostics.Add(InvalidMetadata + ": " + Trim(exception.Message));
                return null;
            }
        }

        return null;
    }

    private const int SafeCoreMetadataLimit = 8 * 1024 * 1024;

    private static string Trim(string? value) =>
        string.IsNullOrEmpty(value) ? "invalid metadata" : value.Length <= 512 ? value : value[..512] + "...";
}

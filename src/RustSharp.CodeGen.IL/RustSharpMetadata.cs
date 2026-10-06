using System.Collections.Immutable;
using System.Diagnostics;
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

/// <summary>A field in a versioned Rust# nominal value layout.</summary>
public sealed record RustSharpMetadataField(string Name, string Type);

/// <summary>
/// A closed nominal value layout exported by a producer. Field order is part
/// of the source contract and is therefore retained in the metadata document.
/// </summary>
public sealed record RustSharpMetadataValueType(
    string Name,
    IEnumerable<RustSharpMetadataField>? Fields = null);

/// <summary>A source-visible struct field with its exact structural source type.</summary>
public sealed record RustSharpMetadataSourceField(string Name, string Type, bool IsPublic = true)
{
    public bool RequiresStaticLifetime { get; init; }
}

/// <summary>One enum variant with its physical offset and ordered original payload fields.</summary>
public sealed record RustSharpMetadataSourceVariant(
    string Name,
    int Discriminant,
    int FieldOffset,
    IEnumerable<RustSharpMetadataSourceField> Fields)
{
    public string ConstructorKind { get; init; } = "unit";
}

/// <summary>A closed source struct identity reconciled with an emitted CLR value layout.</summary>
public sealed record RustSharpMetadataSourceValueType(
    string Name,
    string ClrName,
    IEnumerable<RustSharpMetadataSourceField> Fields,
    bool IsCopy,
    string? DropFunctionId = null)
{
    public bool IsPublic { get; init; } = true;
    public string ConstructorKind { get; init; } = "named";
    /// <summary>Optional owner proof for a source identity re-exported by this package.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RustSharpMetadataSourceOwner? Owner { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<RustSharpMetadataSourceVariant>? Variants { get; init; }
}

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

/// <summary>
/// Explicit ownership and panic terms for one imported call. CLR signatures
/// cannot carry these language-level terms, so consumers validate this record
/// before emitting a cross-package MemberRef.
/// </summary>
public sealed record RustSharpMetadataCallContract(
    string FunctionId,
    string PanicStrategy,
    IEnumerable<string>? ParameterContracts = null,
    string? ReturnContract = null)
{
    public const string ScalarSchema = "rustsharp-scalar-call-v1";
    public const string SourceSchema = "rustsharp-source-call-v1";

    /// <summary>An explicit schema for complete source-generated call terms.</summary>
    public string? Schema { get; init; }

    /// <summary>Ordered canonical source parameter types; CLR object signatures never substitute for these.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? SourceParameterTypes { get; init; }

    /// <summary>Parameter-position static lifetime requirements, independent of erased CLR signatures.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<bool>? SourceParameterStaticLifetimes { get; init; }

    /// <summary>The canonical source return type.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceReturnType { get; init; }

    /// <summary>Ordered explicit return origins, including versioned composite projection paths.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? ReturnOrigins { get; init; }

    /// <summary>Possible active enum variants at each exact returned aggregate position.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<RustSharpMetadataSourceReturnVariant>? SourceReturnVariants { get; init; }
}

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

    /// <summary>Verified owner assembly paths used to resolve re-exported source layouts.</summary>
    public IReadOnlyDictionary<string, string> ResolvedOwnerPaths { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
    public const int MaximumCallContracts = 4096;
    public const int MaximumCallTermsPerFunction = 256;
    public const int MaximumValueTypes = 4096;
    public const int MaximumFieldsPerValueType = 256;
    public const int MaximumJsonCharacters = 1_000_000;

    public RustSharpMetadataDocument(
        string profile,
        string sourceSha256,
        IEnumerable<RustSharpMetadataFunction>? functions = null,
        IEnumerable<RustSharpMetadataGenericInstance>? genericInstances = null,
        IEnumerable<string>? traitImplementations = null,
        string? mirSnapshot = null,
        IEnumerable<RustSharpMetadataOwnershipFunction>? ownership = null,
        string? cleanupSnapshot = null,
        IEnumerable<RustSharpMetadataCallContract>? callContracts = null,
        IEnumerable<RustSharpMetadataValueType>? valueTypes = null,
        IEnumerable<RustSharpMetadataSourceValueType>? sourceValueTypes = null,
        IEnumerable<RustSharpMetadataMethodBody>? emittedMethodBodies = null,
        IEnumerable<RustSharpMetadataSourceStructuralType>? sourceStructuralTypes = null)
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
        CallContracts = NormalizeCallContracts(callContracts);
        ValueTypes = NormalizeValueTypes(valueTypes);
        SourceValueTypes = NormalizeSourceValueTypes(sourceValueTypes);
        EmittedMethodBodies = NormalizeMethodBodies(emittedMethodBodies);
        SourceStructuralTypes = NormalizeSourceStructuralTypes(sourceStructuralTypes).ToImmutableArray();
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
    /// <summary>Explicit imported-call ownership and panic terms.</summary>
    public ImmutableArray<RustSharpMetadataCallContract> CallContracts { get; }
    /// <summary>Closed nominal layouts used by aggregate MemberRefs.</summary>
    public ImmutableArray<RustSharpMetadataValueType> ValueTypes { get; }
    /// <summary>Source struct identities and ordered source fields.</summary>
    [JsonIgnore]
    public ImmutableArray<RustSharpMetadataSourceValueType> SourceValueTypes { get; }
    [JsonInclude, JsonPropertyName("sourceValueTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal ImmutableArray<RustSharpMetadataSourceValueType>? SerializedSourceValueTypes =>
        SourceValueTypes.IsEmpty ? null : SourceValueTypes;
    /// <summary>Actual CLR method body fingerprints required by complete source contracts.</summary>
    [JsonIgnore]
    public ImmutableArray<RustSharpMetadataMethodBody> EmittedMethodBodies { get; }
    [JsonInclude, JsonPropertyName("emittedMethodBodies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal ImmutableArray<RustSharpMetadataMethodBody>? SerializedEmittedMethodBodies =>
        EmittedMethodBodies.IsEmpty ? null : EmittedMethodBodies;
    /// <summary>Foreign structural shapes bound to the independent original producer's CLR layouts.</summary>
    [JsonIgnore]
    public ImmutableArray<RustSharpMetadataSourceStructuralType> SourceStructuralTypes { get; }
    [JsonInclude, JsonPropertyName("sourceStructuralTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal ImmutableArray<RustSharpMetadataSourceStructuralType>? SerializedSourceStructuralTypes =>
        SourceStructuralTypes.IsEmpty ? null : SourceStructuralTypes;
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
        string? cleanupSnapshot = null,
        IEnumerable<RustSharpMetadataCallContract>? callContracts = null,
        IEnumerable<RustSharpMetadataValueType>? valueTypes = null,
        IEnumerable<RustSharpMetadataSourceValueType>? sourceValueTypes = null,
        IEnumerable<RustSharpMetadataSourceStructuralType>? sourceStructuralTypes = null)
    {
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count > MaximumFunctions) throw new ArgumentException("Too many methods.", nameof(methods));
        var linkClock = Stopwatch.StartNew();
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

        IEnumerable<RustSharpMetadataCallContract>? linkedContracts = callContracts?.Select(contract =>
        {
            // Ownership evidence may use the internal `#value` body identity,
            // while the emitted method carries the source identity. Resolve
            // the contract against the actual function table so a producer
            // never publishes a self-referential but unimportable term.
            RustSharpMetadataFunction? target = ResolveTarget(contract.FunctionId);
            return target is null
                ? contract
                : contract with { FunctionId = LinkedIdentity(target) };
        });

        IEnumerable<RustSharpMetadataOwnershipFunction>? linkedOwnership = ownership?.Select(value =>
        {
            // The ownership pass may report its internal body identity with a
            // `#value` suffix. Persist the emitted source identity so consumers
            // can correlate the evidence with the generated MethodDef.
            RustSharpMetadataFunction? target = ResolveTarget(value.FunctionId);
            return target is null
                ? value
                : value with { FunctionId = LinkedIdentity(target) };
        });

        return new(profile, Convert.ToHexString(SHA256.HashData(sourceBytes)), functions,
            genericInstances, traitImplementations, mirSnapshot, linkedOwnership, cleanupSnapshot, linkedContracts,
            valueTypes, sourceValueTypes, sourceStructuralTypes: sourceStructuralTypes);

        RustSharpMetadataFunction? ResolveTarget(string functionId)
        {
            CheckLinkBudget();
            // A closed CLR identity takes precedence over a source alias shared
            // by several generic instances. Source-only evidence cannot choose
            // one of those instances without its emitted specialization ID.
            RustSharpMetadataFunction? emitted = functions.FirstOrDefault(function => function.Name == functionId);
            if (emitted is not null) return emitted;
            RustSharpMetadataFunction[] matches = functions.Where(function =>
                function.SourceQualifiedName == functionId ||
                function.SourceQualifiedName == functionId + "#value" ||
                function.SourceQualifiedName + "#value" == functionId).Take(2).ToArray();
            if (matches.Length > 1)
                throw new ArgumentException("Ambiguous source call evidence requires an emitted CLR specialization ID.", nameof(callContracts));
            return matches.FirstOrDefault();
        }

        string LinkedIdentity(RustSharpMetadataFunction target)
        {
            CheckLinkBudget();
            return target.SourceQualifiedName is { } source &&
                functions.Count(function => function.SourceQualifiedName == source) == 1
                    ? source : target.Name;
        }

        void CheckLinkBudget()
        {
            if (linkClock.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Source-to-CLR metadata linking exceeded its bounded budget.");
        }
    }

    /// <summary>
    /// Returns a document reconciled with the actual LIR layouts. A producer
    /// cannot omit layouts when it emits nominal values, and a supplied layout
    /// list must match declaration order exactly.
    /// </summary>
    public RustSharpMetadataDocument WithValueTypes(IEnumerable<RustSharpMetadataValueType> valueTypes)
    {
        ArgumentNullException.ThrowIfNull(valueTypes);
        RustSharpMetadataValueType[] actual = NormalizeValueTypes(valueTypes).ToArray();
        if (ValueTypes.Length != 0 && !ValueTypesEqual(ValueTypes, actual))
            throw new ArgumentException("Rust# metadata value layouts do not match emitted layouts.", nameof(valueTypes));
        return ValueTypes.Length == 0
            ? new(Profile, SourceSha256, Functions, GenericInstances, TraitImplementations, MirSnapshot,
                Ownership, CleanupSnapshot, CallContracts, actual, SourceValueTypes, EmittedMethodBodies, SourceStructuralTypes)
            : this;
    }

    private static bool ValueTypesEqual(
        IReadOnlyList<RustSharpMetadataValueType> expected,
        RustSharpMetadataValueType[] actual)
    {
        if (expected.Count != actual.Length) return false;
        for (int index = 0; index < expected.Count; index++)
        {
            RustSharpMetadataValueType left = expected[index];
            RustSharpMetadataValueType right = actual[index];
            RustSharpMetadataField[] leftFields = left.Fields?.ToArray() ?? [];
            RustSharpMetadataField[] rightFields = right.Fields?.ToArray() ?? [];
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                leftFields.Length != rightFields.Length)
                return false;
            for (int fieldIndex = 0; fieldIndex < leftFields.Length; fieldIndex++)
            {
                if (!string.Equals(leftFields[fieldIndex].Name, rightFields[fieldIndex].Name, StringComparison.Ordinal) ||
                    !string.Equals(leftFields[fieldIndex].Type, rightFields[fieldIndex].Type, StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    private static ImmutableArray<RustSharpMetadataFunction> NormalizeFunctions(IEnumerable<RustSharpMetadataFunction>? values)
    {
        if (values is null) return [];
        RustSharpMetadataFunction[] result = Materialize(values, MaximumFunctions, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# metadata function count is out of bounds.", nameof(values));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RustSharpMetadataFunction value in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Signature);
            if (value.Name.Length > 4096 || value.Signature.Length > 4096)
                throw new ArgumentException("Rust# metadata function identity is too long.", nameof(values));
            if (!names.Add(value.Name))
                throw new ArgumentException("Rust# metadata function names must be unique.", nameof(values));
            if (value.SourceQualifiedName is not null &&
                (string.IsNullOrWhiteSpace(value.SourceQualifiedName) || value.SourceQualifiedName.Length > 4096))
                throw new ArgumentException("Rust# metadata source function identity is invalid.", nameof(values));
            // A source item may have several closed generic instances or
            // overloads.  Its source identity is unique only together with
            // the declared signature; rejecting the source name alone would
            // make valid monomorphized functions impossible to emit.
            if (value.SourceQualifiedName is not null &&
                !sourceNames.Add(value.SourceQualifiedName + "\u001f" + value.Signature))
                throw new ArgumentException("Rust# metadata source function identities must be unique for a signature.", nameof(values));
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

            // These sequences are evidence, not sets. Preserve declaration
            // order and repeated terms (for example two equal borrow facts
            // attached to different MIR positions) so a consumer can replay
            // the producer contract exactly.
            return result.ToArray();
        }
    }

    private static ImmutableArray<RustSharpMetadataCallContract> NormalizeCallContracts(
        IEnumerable<RustSharpMetadataCallContract>? values)
    {
        if (values is null) return [];
        RustSharpMetadataCallContract[] result = Materialize(values, MaximumCallContracts, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# call contract count is out of bounds.", nameof(values));

        var normalized = new List<RustSharpMetadataCallContract>(result.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var contractClock = Stopwatch.StartNew();
        foreach (RustSharpMetadataCallContract value in result)
        {
            if (contractClock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Rust# call contracts exceeded their normalization time budget.", nameof(values));
            ArgumentException.ThrowIfNullOrWhiteSpace(value.FunctionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.PanicStrategy);
            if (value.FunctionId.Length > 4096 || value.PanicStrategy.Length > 64 ||
                (!string.Equals(value.PanicStrategy, "unwind", StringComparison.Ordinal) &&
                 !string.Equals(value.PanicStrategy, "abort", StringComparison.Ordinal)))
            {
                throw new ArgumentException("Rust# call contract identity or panic strategy is invalid.", nameof(values));
            }

            if (!identities.Add(value.FunctionId))
                throw new ArgumentException("Rust# call contract function IDs must be unique.", nameof(values));

            string[] parameters = NormalizeTerms(value.ParameterContracts, "parameter contracts");
            string? returnContract = value.ReturnContract;
            if (returnContract is not null)
            {
                if (string.IsNullOrWhiteSpace(returnContract) || returnContract.Length > 128 ||
                    returnContract.Contains('\0'))
                    throw new ArgumentException("Rust# call contract return term is invalid.", nameof(values));
                returnContract = returnContract.Trim();
            }

            if (value.Schema is { } schema &&
                (string.IsNullOrWhiteSpace(schema) || schema.Length > 128 || schema.Contains('\0')))
                throw new ArgumentException("Rust# call contract schema is invalid.", nameof(values));
            normalized.Add(new(value.FunctionId, value.PanicStrategy, parameters, returnContract)
            {
                Schema = value.Schema,
                SourceParameterTypes = NormalizeSourceTypes(value.SourceParameterTypes),
                SourceParameterStaticLifetimes = value.SourceParameterStaticLifetimes is null ? null :
                    Materialize(value.SourceParameterStaticLifetimes, MaximumCallTermsPerFunction, "source static lifetime requirements"),
                SourceReturnType = value.SourceReturnType is null ? null :
                    SafeCoreSourceTypeCodec.Format(SafeCoreSourceTypeCodec.Parse(value.SourceReturnType)),
                ReturnOrigins = NormalizeOrigins(value.ReturnOrigins),
                SourceReturnVariants = NormalizeReturnVariants(value.SourceReturnVariants),
            });
        }

        return [.. normalized.OrderBy(static value => value.FunctionId, StringComparer.Ordinal)];

        static string[] NormalizeTerms(IEnumerable<string>? terms, string label)
        {
            if (terms is null) return [];
            string[] result = Materialize(terms, MaximumCallTermsPerFunction, label);
            if (result.Any(static term => string.IsNullOrWhiteSpace(term) || term.Length > 128 || term.Contains('\0')))
                throw new ArgumentException($"Rust# call contract {label} are invalid.", nameof(values));
            // Parameter contracts are positional: equal terms at different
            // argument indices must remain separate and in declaration order.
            return [.. result.Select(static term => term.Trim())];
        }

        static string[]? NormalizeSourceTypes(IEnumerable<string>? types)
        {
            if (types is null) return null;
            string[] result = Materialize(types, MaximumCallTermsPerFunction, "source parameter types");
            var clock = Stopwatch.StartNew();
            for (int index = 0; index < result.Length; index++)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new ArgumentException("Rust# source parameter types exceeded their normalization time budget.");
                result[index] = SafeCoreSourceTypeCodec.Format(SafeCoreSourceTypeCodec.Parse(result[index]));
            }
            return result;
        }

        static string[]? NormalizeOrigins(IEnumerable<string>? origins)
        {
            if (origins is null) return null;
            string[] result = Materialize(origins, MaximumCallTermsPerFunction, "return origins");
            var clock = Stopwatch.StartNew();
            foreach (string origin in result)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new ArgumentException("Rust# source return origins exceeded their normalization time budget.");
                SafeCoreSourceOriginCodec.Parse(origin);
            }
            return result;
        }
    }

    private static ImmutableArray<RustSharpMetadataSourceValueType> NormalizeSourceValueTypes(
        IEnumerable<RustSharpMetadataSourceValueType>? values)
    {
        if (values is null) return [];
        RustSharpMetadataSourceValueType[] result = Materialize(values, MaximumValueTypes, nameof(values));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var clrNames = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<RustSharpMetadataSourceValueType>(result.Length);
        var clock = Stopwatch.StartNew();
        foreach (RustSharpMetadataSourceValueType value in result)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Rust# source layouts exceeded their time budget.", nameof(values));
            if (value is null || SafeCoreSourceTypeCodec.Parse(value.Name).Kind != SafeCoreSemanticTypeKind.Adt ||
                !names.Add(value.Name) || string.IsNullOrWhiteSpace(value.ClrName) || value.ClrName.Length > 1024 ||
                value.ClrName.Contains('\0') || !clrNames.Add(value.ClrName) ||
                value.DropFunctionId is { } drop && (string.IsNullOrWhiteSpace(drop) || drop.Length > 4096) ||
                value.IsCopy && value.DropFunctionId is not null)
                throw new ArgumentException("Rust# source layout identity or Copy/Drop terms are invalid.", nameof(values));
            if (value.Owner is { } owner) ValidateSourceOwner(owner);
            RustSharpMetadataSourceField[] fields = Materialize(value.Fields, MaximumFieldsPerValueType, "source fields");
            if (value.ConstructorKind is not ("named" or "tuple" or "unit" or "enum") ||
                value.ConstructorKind == "unit" && fields.Length != 0)
                throw new ArgumentException("Rust# source constructor kind is invalid or contradicts its fields.", nameof(values));
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (RustSharpMetadataSourceField field in fields)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new ArgumentException("Rust# source fields exceeded their time budget.", nameof(values));
                if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 1024 ||
                    field.Name.Contains('\0') || !fieldNames.Add(field.Name))
                    throw new ArgumentException("Rust# source fields are invalid or duplicated.", nameof(values));
                SafeCoreSourceTypeCodec.Parse(field.Type);
            }
            RustSharpMetadataSourceVariant[]? variants = null;
            if (value.ConstructorKind == "enum")
            {
                variants = Materialize(value.Variants ?? [], MaximumFieldsPerValueType, "source enum variants");
                if (variants.Length == 0 || fields.Length == 0 || fields[0].Name != "$tag" || fields[0].Type != "i32")
                    throw new ArgumentException("Rust# enum source layouts require variants and an i32 tag.", nameof(values));
                var variantNames = new HashSet<string>(StringComparer.Ordinal);
                var discriminants = new HashSet<int>();
                int offset = 1;
                for (int variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(5))
                        throw new ArgumentException("Rust# source variants exceeded their time budget.", nameof(values));
                    RustSharpMetadataSourceVariant variant = variants[variantIndex];
                    if (variant is null || string.IsNullOrWhiteSpace(variant.Name) || variant.Name.Length > 4096 ||
                        !variantNames.Add(variant.Name) || !discriminants.Add(variant.Discriminant) ||
                        variant.FieldOffset != offset || variant.ConstructorKind is not ("named" or "tuple" or "unit"))
                        throw new ArgumentException("Rust# source variant identity, constructor, discriminant or offset is invalid.", nameof(values));
                    RustSharpMetadataSourceField[] payload = Materialize(variant.Fields, MaximumFieldsPerValueType, "source variant fields");
                    if (variant.ConstructorKind == "unit" && payload.Length != 0 || offset + payload.Length > fields.Length)
                        throw new ArgumentException("Rust# source variant fields contradict its constructor or physical layout.", nameof(values));
                    for (int fieldIndex = 0; fieldIndex < payload.Length; fieldIndex++)
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(5))
                            throw new ArgumentException("Rust# source variant fields exceeded their time budget.", nameof(values));
                        RustSharpMetadataSourceField field = payload[fieldIndex];
                        RustSharpMetadataSourceField physical = fields[offset + fieldIndex];
                        if (field is null || physical.Name != "$v" + variantIndex.ToString(CultureInfo.InvariantCulture) + "$" + field.Name ||
                            physical.Type != field.Type || physical.RequiresStaticLifetime != field.RequiresStaticLifetime ||
                            variant.ConstructorKind == "tuple" && field.Name != fieldIndex.ToString(CultureInfo.InvariantCulture))
                            throw new ArgumentException("Rust# source variant payload differs from its physical fields.", nameof(values));
                        SafeCoreSourceTypeCodec.Parse(field.Type);
                    }
                    variants[variantIndex] = variant with { Fields = payload };
                    offset += payload.Length;
                }
                if (offset != fields.Length)
                    throw new ArgumentException("Rust# source enum variants do not account for every physical field.", nameof(values));
            }
            else if (value.Variants is not null)
                throw new ArgumentException("Rust# non-enum source layouts cannot contain variant evidence.", nameof(values));
            normalized.Add(value with { Fields = fields, Variants = variants });
        }
        return [.. normalized.OrderBy(static value => value.Name, StringComparer.Ordinal)];

    }

    private static ImmutableArray<RustSharpMetadataValueType> NormalizeValueTypes(
        IEnumerable<RustSharpMetadataValueType>? values)
    {
        if (values is null) return [];
        RustSharpMetadataValueType[] result = Materialize(values, MaximumValueTypes, nameof(values));
        if (result.Any(static value => value is null))
            throw new ArgumentException("Rust# value layout count is out of bounds.", nameof(values));

        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<RustSharpMetadataValueType>(result.Length);
        foreach (RustSharpMetadataValueType value in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Name);
            if (value.Name.Length > 1024 || !names.Add(value.Name))
                throw new ArgumentException("Rust# value layout identities must be unique and bounded.", nameof(values));
            RustSharpMetadataField[] fields = Materialize(
                value.Fields ?? [], MaximumFieldsPerValueType, "value fields");
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (RustSharpMetadataField field in fields)
            {
                if (field is null || string.IsNullOrWhiteSpace(field.Name) ||
                    string.IsNullOrWhiteSpace(field.Type) || field.Name.Length > 1024 ||
                    field.Type.Length > 1024 || !fieldNames.Add(field.Name) ||
                    field.Type.Contains('\0'))
                    throw new ArgumentException("Rust# value layout fields are invalid or duplicated.", nameof(values));
            }
            normalized.Add(new(value.Name, fields));
        }

        return [.. normalized.OrderBy(static value => value.Name, StringComparer.Ordinal)];
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
        WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Metadata,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true)]
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
        if (parsed.Functions is null || parsed.GenericInstances is null || parsed.TraitImplementations is null)
            throw new ArgumentException("Rust# metadata core collections are incomplete.", nameof(json));

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
            parsed.CleanupSnapshot,
            parsed.CallContracts,
            parsed.ValueTypes,
            parsed.SourceValueTypes,
            parsed.EmittedMethodBodies,
            parsed.SourceStructuralTypes);
        // Older v1 producers predate the ownership extension and therefore do
        // not contain an `ownership` property. Return the canonical document
        // while accepting that additive shape for cross-package compatibility.
        return canonical;
    }

    private sealed class MetadataPayload
    {
        [JsonRequired]
        public string? Schema { get; set; }
        [JsonRequired]
        public string? Profile { get; set; }
        [JsonRequired]
        public string? SourceSha256 { get; set; }
        [JsonRequired]
        public RustSharpMetadataFunction[]? Functions { get; set; }
        [JsonRequired]
        public RustSharpMetadataGenericInstance[]? GenericInstances { get; set; }
        [JsonRequired]
        public string[]? TraitImplementations { get; set; }
        public string? MirSnapshot { get; set; }
        public RustSharpMetadataOwnershipFunction[]? Ownership { get; set; }
        public string? CleanupSnapshot { get; set; }
        public RustSharpMetadataCallContract[]? CallContracts { get; set; }
        public RustSharpMetadataValueType[]? ValueTypes { get; set; }
        public RustSharpMetadataSourceValueType[]? SourceValueTypes { get; set; }
        public RustSharpMetadataMethodBody[]? EmittedMethodBodies { get; set; }
        public RustSharpMetadataSourceStructuralType[]? SourceStructuralTypes { get; set; }
    }
}

/// <summary>Reads the embedded AssemblyMetadataAttribute without loading generated code.</summary>
public static class RustSharpMetadataReader
{
    public const string AttributeKey = "RustSharp.Metadata.v1";

    public static string? FindJson(System.Reflection.Metadata.MetadataReader metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string? found = null;
        foreach (System.Reflection.Metadata.CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            System.Reflection.Metadata.CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            string? name = TryGetAttributeTypeName(metadata, attribute.Constructor);
            if (!string.Equals(name, "System.Reflection.AssemblyMetadataAttribute", StringComparison.Ordinal)) continue;
            string? value = TryDecodeValue(metadata, attribute.Value);
            if (value is null || !value.StartsWith(AttributeKey + "\u001f", StringComparison.Ordinal))
                continue;

            string json = value[(AttributeKey.Length + 1)..];
            if (found is not null)
                throw new InvalidDataException("The assembly contains duplicate Rust# metadata attributes.");
            found = json;
        }

        return found;
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
public static partial class RustSharpMetadataConsumer
{
    public const string MissingMetadata = "RSC0010";
    public const string InvalidMetadata = "RSC0011";
    public const string ProfileMismatch = "RSC0012";
    public const string ExportMissing = "RSC0013";
    public const int MaximumAssemblyBytes = 256 * 1024 * 1024;

    public static RustSharpMetadataImportResult ReadAssembly(
        string assemblyPath,
        string? expectedProfile = null,
        IEnumerable<string>? requiredFunctions = null,
        IEnumerable<string>? dependencyPaths = null,
        CancellationToken cancellationToken = default) =>
        ReadAssemblyCore(assemblyPath, expectedProfile, requiredFunctions, new OwnerReadContext(dependencyPaths, cancellationToken));

    private static RustSharpMetadataImportResult ReadAssemblyCore(
        string assemblyPath,
        string? expectedProfile,
        IEnumerable<string>? requiredFunctions,
        OwnerReadContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);
        var diagnostics = new List<string>();
        RustSharpMetadataDocument? document = null;
        string? generic = null;
        Guid? mvid = null;
        string? assemblyName = null;
        var resolvedOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        bool entered = false;
        try
        {
            context.Enter(fullPath);
            entered = true;
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
                ResolveOwnerBindings(metadata, document, fullPath, assemblyName, context, resolvedOwners, diagnostics);
                context.Check();
                ValidateGeneratedFunctions(metadata, document, diagnostics);
                context.Check();
                RustSharpMetadataMethodBodies.Validate(pe, metadata, document, diagnostics, context.CancellationToken);
                context.Check();
                ValidateGeneratedValueTypes(metadata, document, diagnostics);
                context.Check();
                ValidateSourceValueTypes(document, diagnostics);
                context.Check();
                ValidateOwnershipContracts(document, diagnostics);
                context.Check();
                ValidateCallContracts(document, diagnostics);
                context.Check();
                ValidateRequiredFunctions(document, requiredFunctions, diagnostics);
                context.Check();
            }

            ModuleDefinition module = metadata.GetModuleDefinition();
            mvid = metadata.GetGuid(module.Mvid);
            generic = TryReadGenericResource(pe, metadata, diagnostics);
            context.Check();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            BadImageFormatException or InvalidDataException or ArgumentException or JsonException or
            EndOfStreamException or OverflowException or InvalidOperationException or NotSupportedException)
        {
            diagnostics.Add(InvalidMetadata + ": " + Trim(exception.Message));
        }
        finally
        {
            if (entered) context.Leave(fullPath);
        }

        return new(fullPath, document, generic, mvid, diagnostics.AsReadOnly())
        {
            AssemblyName = assemblyName,
            ResolvedOwnerPaths = resolvedOwners,
        };
    }

    public static RustSharpMetadataFunction? FindFunction(
        RustSharpMetadataDocument document, string functionId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionId);
        // A source declaration may have several closed MethodDefs. An exact
        // emitted identity must win even if an earlier entry shares its name
        // as a source alias; a source-only ID may resolve only one MethodDef.
        RustSharpMetadataFunction? emitted = document.Functions.FirstOrDefault(function =>
            string.Equals(function.Name, functionId, StringComparison.Ordinal));
        if (emitted is not null) return emitted;
        RustSharpMetadataFunction[] aliases = document.Functions.Where(function =>
            function.SourceQualifiedName is { } source &&
            (string.Equals(source, functionId, StringComparison.Ordinal) ||
             string.Equals(source, functionId + "#value", StringComparison.Ordinal) ||
             string.Equals(source + "#value", functionId, StringComparison.Ordinal))).Take(2).ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private static void ValidateCallContracts(
        RustSharpMetadataDocument document,
        List<string> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contracted = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        var ownershipPanics = new Dictionary<string, string>(StringComparer.Ordinal);
        SourceMirEvidence? sourceEvidence = document.CallContracts.Any(static contract =>
            contract.Schema == RustSharpMetadataCallContract.SourceSchema)
            ? ReadSourceMirEvidence(document.MirSnapshot) : null;
        foreach (RustSharpMetadataOwnershipFunction ownership in document.Ownership)
        {
            CheckBudget();
            if (FindFunction(document, ownership.FunctionId) is { } target)
                ownershipPanics.TryAdd(target.Name, ownership.PanicStrategy);
        }
        foreach (RustSharpMetadataCallContract contract in document.CallContracts)
        {
            CheckBudget();
            RustSharpMetadataFunction? function = FindFunction(document, contract.FunctionId);
            if (function is null)
            {
                AddContractDiagnostic(diagnostics,
                    "call contract references unknown function '" + Trim(contract.FunctionId) + "'.");
                continue;
            }

            contracted.Add(function.Name);

            // CLR and source IDs are aliases for one callable MethodDef. Two
            // records under different aliases must not supply conflicting facts.
            if (!seen.Add(function.Name))
            {
                AddContractDiagnostic(diagnostics,
                    "call contract for resolved function '" + Trim(function.Name) + "' is duplicated.");
                continue;
            }

            bool sourceSchema = contract.Schema == RustSharpMetadataCallContract.SourceSchema;
            if (contract.Schema is not null && contract.Schema != RustSharpMetadataCallContract.ScalarSchema && !sourceSchema)
                AddContractDiagnostic(diagnostics, "call contract has an unsupported schema.");

            if (!string.Equals(contract.PanicStrategy, "unwind", StringComparison.Ordinal) &&
                !string.Equals(contract.PanicStrategy, "abort", StringComparison.Ordinal))
            {
                AddContractDiagnostic(diagnostics,
                    "call contract for '" + Trim(contract.FunctionId) + "' has an unsupported panic strategy.");
            }

            int arrow = function.Signature.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0 || function.Signature.IndexOf("->", arrow + 2, StringComparison.Ordinal) >= 0)
            {
                AddContractDiagnostic(diagnostics,
                    "call contract target '" + Trim(contract.FunctionId) + "' has an invalid function signature.");
                continue;
            }

            string[] parameters = string.IsNullOrWhiteSpace(function.Signature[..arrow])
                ? [] : function.Signature[..arrow].Split(',', StringSplitOptions.None);
            string returnType = function.Signature[(arrow + 2)..];
            bool sourceScalar = function.SourceQualifiedName is { } source &&
                !string.Equals(source, function.Name, StringComparison.Ordinal) &&
                parameters.All(static type => type is "I32" or "Bool") &&
                returnType is "I32" or "Bool" or "Void";
            if (sourceScalar && contract.Schema != RustSharpMetadataCallContract.ScalarSchema && !sourceSchema)
                AddContractDiagnostic(diagnostics, "source scalar call contract for '" +
                    Trim(contract.FunctionId) + "' requires its explicit scalar schema.");
            string[] terms = (contract.ParameterContracts ?? []).ToArray();
            bool scalarSchema = !sourceSchema && (sourceScalar || contract.Schema == RustSharpMetadataCallContract.ScalarSchema);
            if ((terms.Length != 0 || scalarSchema || sourceSchema) && terms.Length != parameters.Length)
            {
                AddContractDiagnostic(diagnostics,
                    "call contract for '" + Trim(contract.FunctionId) + "' does not cover every parameter.");
            }

            if (sourceSchema)
                ValidateSourceCallContract(document, contract, function, parameters, returnType, terms, sourceEvidence!, diagnostics);
            else if (contract.SourceParameterTypes is not null || contract.SourceParameterStaticLifetimes is not null ||
                contract.SourceReturnType is not null || contract.ReturnOrigins is not null || contract.SourceReturnVariants is not null)
                AddContractDiagnostic(diagnostics, "source call terms require their explicit source schema.");

            for (int index = 0; !sourceSchema && index < Math.Min(terms.Length, parameters.Length); index++)
            {
                if (!TermMatchesType(terms[index], parameters[index], isReturn: false, scalarSchema))
                    AddContractDiagnostic(diagnostics, "call contract parameter " +
                        index.ToString(CultureInfo.InvariantCulture) + " for '" + Trim(contract.FunctionId) +
                        "' is unsupported or contradicts its CLR signature.");
            }

            if (!sourceSchema && ((contract.ReturnContract is null && scalarSchema) ||
                (contract.ReturnContract is { } result &&
                    !TermMatchesType(result, returnType, isReturn: true, scalarSchema))))
                AddContractDiagnostic(diagnostics, "call contract return for '" + Trim(contract.FunctionId) +
                    "' is missing, unsupported or contradicts its CLR signature.");

            if (ownershipPanics.TryGetValue(function.Name, out string? panic) && panic != contract.PanicStrategy)
                AddContractDiagnostic(diagnostics, "call contract panic strategy for '" +
                    Trim(contract.FunctionId) + "' contradicts its ownership evidence.");
        }

        // CLR signatures do not encode Rust# ownership or panic semantics.
        // Require an explicit contract whenever an exported function carries
        // a nominal value or managed reference. Accepting these methods with
        // an omitted contract would let a consumer guess move/borrow terms
        // from CLR value semantics and silently bypass the package boundary.
        foreach (RustSharpMetadataFunction function in document.Functions)
        {
            CheckBudget();
            if (RequiresOwnershipContract(function) && !contracted.Contains(function.Name))
            {
                AddContractDiagnostic(diagnostics,
                    "exported function '" + Trim(function.Name) +
                    "' has a value or reference signature but no ownership call contract.");
            }
        }

        void CheckBudget()
        {
            if (document.CallContracts.Length > RustSharpMetadataDocument.MaximumCallContracts ||
                document.Ownership.Length > RustSharpMetadataDocument.MaximumOwnershipFunctions ||
                clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Imported call contracts exceeded their bounded validation budget.");
        }

        static bool TermMatchesType(string term, string type, bool isReturn, bool scalarSchema)
        {
            if (scalarSchema)
                return type is "I32" or "Bool" ? term == "copy" :
                    isReturn && type == "Void" && term == "unit";
            return term switch
            {
                "copy" => type is "I32" or "Bool",
                "move" => type.StartsWith("Value(", StringComparison.Ordinal),
                "borrow:shared" or "borrow:mut" or "borrow:mutable" =>
                    type.StartsWith('&') || type == "Any",
                "unit" => isReturn && type == "Void",
                _ => false,
            };
        }

        static bool RequiresOwnershipContract(RustSharpMetadataFunction function)
        {
            // Closed generic helper MethodDefs are generated implementation
            // details. Their source declaration's contract is represented by
            // the specialization metadata and is reconciled separately.
            if (function.Name.StartsWith("generic_", StringComparison.Ordinal)) return false;
            string signature = function.Signature;
            // Signatures are canonicalized by the metadata writer. A bounded
            // token scan is sufficient here and avoids accepting arbitrary
            // nested syntax as a contract-bearing type.
            return signature.Contains("Value(", StringComparison.Ordinal) ||
                signature.Contains('&') || signature.Contains("Any", StringComparison.Ordinal);
        }
    }

    private static void ValidateSourceCallContract(
        RustSharpMetadataDocument document,
        RustSharpMetadataCallContract contract,
        RustSharpMetadataFunction function,
        string[] clrParameters,
        string clrReturn,
        string[] terms,
        SourceMirEvidence sourceEvidence,
        List<string> diagnostics)
    {
        if (contract.SourceParameterTypes is null || contract.SourceParameterStaticLifetimes is null || contract.SourceReturnType is null ||
            contract.ReturnOrigins is null || contract.ReturnContract is null)
        {
            AddContractDiagnostic(diagnostics, "source call contract is missing required source types, return terms or origins.");
            return;
        }
        SafeCoreType[] parameters = [.. contract.SourceParameterTypes.Select(static type => SafeCoreSourceTypeCodec.Parse(type))];
        bool[] staticLifetimes = contract.SourceParameterStaticLifetimes.ToArray();
        SafeCoreType result = SafeCoreSourceTypeCodec.Parse(contract.SourceReturnType);
        SourceMirFunction[] sourceFunctions = sourceEvidence.Functions.Where(value =>
            value.Name == function.SourceQualifiedName || value.Name == function.SourceQualifiedName + "#value" ||
            value.Name + "#value" == function.SourceQualifiedName).Take(2).ToArray();
        if (sourceFunctions.Length != 1 || sourceFunctions[0].ReturnType != contract.SourceReturnType ||
            !sourceFunctions[0].ParameterTypes.SequenceEqual(contract.SourceParameterTypes) ||
            !sourceFunctions[0].ParameterStaticLifetimes.SequenceEqual(staticLifetimes))
            AddContractDiagnostic(diagnostics, "source call signature contradicts its independent emitted MIR snapshot.");
        if (staticLifetimes.Length != parameters.Length)
            AddContractDiagnostic(diagnostics, "source call static lifetime terms do not cover every parameter.");
        foreach (SafeCoreType parameter in parameters) ValidateSourceTypeEvidence(document, parameter, 0);
        ValidateSourceTypeEvidence(document, result, 0);
        string[] origins = contract.ReturnOrigins.ToArray();
        if (parameters.Length != clrParameters.Length)
            AddContractDiagnostic(diagnostics, "source call parameter types do not cover the CLR signature.");
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < Math.Min(parameters.Length, clrParameters.Length); index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Source call validation exceeded its time budget.");
            if (SourceClrType(document, parameters[index], isReturn: false) != clrParameters[index])
                AddContractDiagnostic(diagnostics, "source call parameter type contradicts its CLR signature.");
            if (index < terms.Length && !SourceTermMatches(document, terms[index], parameters[index], isReturn: false))
                AddContractDiagnostic(diagnostics, "source call parameter ownership term contradicts its source type.");
        }
        if (SourceClrType(document, result, isReturn: true) != clrReturn)
            AddContractDiagnostic(diagnostics, "source call return type contradicts its CLR signature.");
        if (!SourceTermMatches(document, contract.ReturnContract, result, isReturn: true))
            AddContractDiagnostic(diagnostics, "source call return ownership term contradicts its source type.");
        RustSharpMetadataSourceReturnVariant[] returnedVariants = contract.SourceReturnVariants?.ToArray() ?? [];
        if (sourceFunctions.Length == 1 && !sourceFunctions[0].ReturnVariants.Select(FormatVariant).SequenceEqual(returnedVariants.Select(FormatVariant)))
            AddContractDiagnostic(diagnostics, "Source returned enum variants contradict their independent emitted MIR snapshot.");
        var reachedVariants = new HashSet<int>();
        foreach (string error in SafeCoreSourceOriginCodec.ValidateContract(parameters, result, origins, Fields,
            parameterStaticLifetimes: staticLifetimes, staticFields: StaticFields, returnedFields: ReturnedFields))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Source return origin validation exceeded its time budget.");
            AddContractDiagnostic(diagnostics, error);
        }
        if (reachedVariants.Count != returnedVariants.Length)
            AddContractDiagnostic(diagnostics, "Source return variant evidence names a missing or inactive enum value slot.");

        IReadOnlyList<(string Name, SafeCoreType Type)> Fields(SafeCoreType type)
        {
            RustSharpMetadataSourceValueType? layout = document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name);
            if (layout is null) throw new ArgumentException("Source nominal origins require a declared source value layout.");
            return layout.Fields.Select(static field => (field.Name, SafeCoreSourceTypeCodec.Parse(field.Type))).ToArray();
        }

        IReadOnlyList<(string Name, bool RequiresStaticLifetime)> StaticFields(SafeCoreType type)
        {
            RustSharpMetadataSourceValueType? layout = document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name);
            if (layout is null) throw new ArgumentException("Static source field origins require a declared source value layout.");
            return layout.Fields.Select(static field => (field.Name, field.RequiresStaticLifetime)).ToArray();
        }

        IReadOnlyList<(string Name, SafeCoreType Type)>? ReturnedFields(SafeCoreType type, IReadOnlyList<SafeCoreMirProjection> path)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Source return enum evidence exceeded its time budget.");
            RustSharpMetadataSourceValueType? layout = document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name);
            if (layout?.ConstructorKind != "enum") return null;
            string[] encodedPath = path.Select(static projection => SafeCoreSourceOriginCodec.FormatProjection(projection)).ToArray();
            RustSharpMetadataSourceVariant[] variants = layout.Variants!.ToArray();
            RustSharpMetadataSourceField[] physical = layout.Fields.ToArray();
            var selected = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < returnedVariants.Length; index++)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Source return enum variant count exceeded its time budget.");
                RustSharpMetadataSourceReturnVariant evidence = returnedVariants[index];
                if (!evidence.ValuePath.SequenceEqual(encodedPath)) continue;
                reachedVariants.Add(index);
                RustSharpMetadataSourceVariant? variant = variants.FirstOrDefault(value => value.Name == evidence.VariantName);
                if (variant is null) throw new ArgumentException("Source returned enum evidence selects an undeclared variant.");
                int count = variant.Fields.Count();
                for (int field = 0; field < count; field++) selected.Add(physical[variant.FieldOffset + field].Name);
            }
            if (!returnedVariants.Any(value => value.ValuePath.SequenceEqual(encodedPath)))
                throw new ArgumentException("Source enum return slot is missing its possible active variant evidence.");
            return physical.Where(value => selected.Contains(value.Name)).Select(static field =>
                (field.Name, SafeCoreSourceTypeCodec.Parse(field.Type))).ToArray();
        }

        static string FormatVariant(RustSharpMetadataSourceReturnVariant value) => SafeCoreSourceOriginCodec.FormatReturnedVariant(
            value.ValuePath.Select(static projection => SafeCoreSourceOriginCodec.ParseProjection(projection)).ToArray(), value.VariantName);
    }

    private static bool SourceTermMatches(RustSharpMetadataDocument document, string term, SafeCoreType type, bool isReturn) =>
        type.Kind switch
        {
            SafeCoreSemanticTypeKind.Unit => term == (isReturn ? "unit" : "copy"),
            SafeCoreSemanticTypeKind.Bool or SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize => term == "copy",
            SafeCoreSemanticTypeKind.Reference => term == (type.IsMutable ? "borrow:mut" : "borrow:shared"),
            SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Adt =>
                term == (SourceIsCopy(document, type, 0) ? "copy" : "move"),
            _ => false,
        };

    private static void ValidateSourceTypeEvidence(RustSharpMetadataDocument document, SafeCoreType type, int depth,
        Stopwatch? clock = null)
    {
        clock ??= Stopwatch.StartNew();
        if (depth >= SafeCoreSourceTypeCodec.MaximumDepth || clock.Elapsed > TimeSpan.FromSeconds(5))
            throw new InvalidDataException("Source signature evidence exceeded its depth budget.");
        if (type.Kind == SafeCoreSemanticTypeKind.Adt && !document.SourceValueTypes.Any(value => value.Name == type.Name))
            throw new InvalidDataException("Source nominal signature has no declared source layout.");
        if (type.Kind == SafeCoreSemanticTypeKind.Array && type.Length > RustSharpMetadataDocument.MaximumFieldsPerValueType)
            throw new InvalidDataException("Source array signature exceeds its field budget.");
        foreach (SafeCoreType child in type.Elements) ValidateSourceTypeEvidence(document, child, depth + 1, clock);
    }

    private static bool SourceIsCopy(RustSharpMetadataDocument document, SafeCoreType type, int depth,
        Stopwatch? clock = null)
    {
        clock ??= Stopwatch.StartNew();
        if (depth >= SafeCoreSourceTypeCodec.MaximumDepth || clock.Elapsed > TimeSpan.FromSeconds(5))
            throw new InvalidDataException("Source Copy evidence exceeds its depth budget.");
        return type.Kind switch
        {
            SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Bool or SafeCoreSemanticTypeKind.I32 or
                SafeCoreSemanticTypeKind.Usize => true,
            SafeCoreSemanticTypeKind.Reference => !type.IsMutable,
            SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array =>
                type.Elements.All(element => SourceIsCopy(document, element, depth + 1, clock)),
            SafeCoreSemanticTypeKind.Adt => document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name)?.IsCopy == true,
            _ => false,
        };
    }


    private static string SourceClrType(RustSharpMetadataDocument document, SafeCoreType type, bool isReturn)
    {
        if (isReturn && type.Kind == SafeCoreSemanticTypeKind.Unit) return "Void";
        if (type.Kind is SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize) return "I32";
        if (type.Kind == SafeCoreSemanticTypeKind.Bool) return "Bool";
        if (type.Kind == SafeCoreSemanticTypeKind.Reference) return "Any";
        if (type.Kind == SafeCoreSemanticTypeKind.Adt)
        {
            RustSharpMetadataSourceValueType? layout = document.SourceValueTypes.FirstOrDefault(value => value.Name == type.Name);
            if (layout is null) throw new InvalidDataException("Source nominal signature has no declared source layout.");
            return "Value(" + layout.ClrName + ")";
        }
        if (type.Kind is SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array)
        {
            if (type.Kind == SafeCoreSemanticTypeKind.Array && type.Length > RustSharpMetadataDocument.MaximumFieldsPerValueType)
                throw new InvalidDataException("Source array layout exceeds its field budget.");
            RustSharpMetadataSourceStructuralType? imported = document.SourceStructuralTypes.FirstOrDefault(value => value.Type == type.ToString());
            if (imported is not null) return "Value(" + imported.ClrName + ")";
            string name = "mir_value_" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(type.Kind + "|" + type))).ToLowerInvariant()[..24];
            if (!document.ValueTypes.Any(value => value.Name == name))
                throw new InvalidDataException("Source structural signature has no emitted CLR layout.");
            return "Value(" + name + ")";
        }
        throw new InvalidDataException("Unsupported source signature shape.");
    }

    private static void ValidateSourceValueTypes(RustSharpMetadataDocument document, List<string> diagnostics)
    {
        var clock = Stopwatch.StartNew();
        SourceMirEvidence? evidence = document.SourceValueTypes.IsEmpty ? null : ReadSourceMirEvidence(document.MirSnapshot);
        foreach (RustSharpMetadataSourceValueType source in document.SourceValueTypes)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Source layouts exceeded their reconciliation time budget.");
            RustSharpMetadataValueType? clr = document.ValueTypes.FirstOrDefault(value => value.Name == source.ClrName);
            RustSharpMetadataSourceField[] fields = source.Fields.ToArray();
            SourceMirLayout[] sourceLayouts = evidence!.Layouts.Where(value => value.Name == source.Name).Take(2).ToArray();
            if (sourceLayouts.Length != 1 || sourceLayouts[0].IsCopy != source.IsCopy ||
                sourceLayouts[0].Fields.Count != fields.Length ||
                sourceLayouts[0].Variants.Count != (source.Variants?.Count() ?? 0))
                AddContractDiagnostic(diagnostics, "source layout identity or Copy/variant/static terms contradict supported emitted MIR evidence.");
            if (sourceLayouts.Length == 1)
            {
                RustSharpMetadataSourceVariant[] variants = source.Variants?.ToArray() ?? [];
                for (int variantIndex = 0; variantIndex < Math.Min(variants.Length, sourceLayouts[0].Variants.Count); variantIndex++)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(5))
                        throw new InvalidDataException("Source variants exceeded their reconciliation time budget.");
                    SourceMirVariant actual = sourceLayouts[0].Variants[variantIndex];
                    RustSharpMetadataSourceVariant variant = variants[variantIndex];
                    if (actual.Name != variant.Name || actual.Discriminant != variant.Discriminant ||
                        actual.Offset != variant.FieldOffset || actual.FieldCount != variant.Fields.Count())
                        AddContractDiagnostic(diagnostics, "source enum variant order, identity, discriminant or offset contradicts its MIR snapshot.");
                }
            }
            RustSharpMetadataField[] clrFields = clr?.Fields?.ToArray() ?? [];
            if (source.Owner is not null) continue;
            if (clr is null || fields.Length != clrFields.Length)
            {
                AddContractDiagnostic(diagnostics, "source value layout does not match an emitted CLR value layout.");
                continue;
            }
            for (int index = 0; index < fields.Length; index++)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Source fields exceeded their reconciliation time budget.");
                SafeCoreType fieldType = SafeCoreSourceTypeCodec.Parse(fields[index].Type);
                ValidateSourceTypeEvidence(document, fieldType, 0);
                if (sourceLayouts.Length == 1 && index < sourceLayouts[0].Fields.Count &&
                    (sourceLayouts[0].Fields[index].Name != fields[index].Name || sourceLayouts[0].Fields[index].Type != fields[index].Type ||
                     sourceLayouts[0].Fields[index].RequiresStaticLifetime != fields[index].RequiresStaticLifetime))
                    AddContractDiagnostic(diagnostics, "source field order or type contradicts its independent emitted MIR snapshot.");
                if (fields[index].Name != clrFields[index].Name ||
                    SourceClrType(document, fieldType, isReturn: false) != clrFields[index].Type)
                    AddContractDiagnostic(diagnostics, "source field order or type contradicts its CLR layout.");
                if (source.IsCopy && !SourceIsCopy(document, fieldType, 0))
                    AddContractDiagnostic(diagnostics, "source Copy value contains a non-Copy field.");
            }
            if (source.DropFunctionId is { } destructor)
            {
                RustSharpMetadataFunction? function = FindFunction(document, destructor);
                if (function is null || function.Signature != "Any->Void")
                    AddContractDiagnostic(diagnostics, "source Drop method is missing or has an incompatible checked receiver signature.");
            }
        }
    }

    private sealed record SourceMirEvidence(List<SourceMirFunction> Functions, List<SourceMirLayout> Layouts);
    private sealed record SourceMirFunction(string Name, string ReturnType, List<string> ParameterTypes, List<bool> ParameterStaticLifetimes)
    {
        public List<RustSharpMetadataSourceReturnVariant> ReturnVariants { get; } = [];
    }
    private sealed record SourceMirVariant(string Name, int Discriminant, int Offset, int FieldCount);
    private sealed record SourceMirLayout(string Name, bool IsCopy, List<RustSharpMetadataSourceField> Fields)
    {
        public List<SourceMirVariant> Variants { get; } = [];
    }

    private static SourceMirEvidence ReadSourceMirEvidence(string? snapshot)
    {
        const int maximumLines = 32768;
        if (snapshot is null || !(snapshot.StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal) ||
            snapshot.StartsWith("safe-core-mir-v2\n", StringComparison.Ordinal) ||
            snapshot.StartsWith("safe-core-mir-v3\n", StringComparison.Ordinal)))
            throw new InvalidDataException("Source package contracts require a supported independent MIR snapshot.");
        string[] lines = snapshot.Split('\n', maximumLines + 1, StringSplitOptions.None);
        if (lines.Length > maximumLines)
            throw new InvalidDataException("Source package MIR snapshot exceeds its line budget.");
        var result = new SourceMirEvidence([], []);
        SourceMirFunction? function = null;
        SourceMirLayout? layout = null;
        var clock = Stopwatch.StartNew();
        foreach (string line in lines)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Source package MIR snapshot exceeded its parsing time budget.");
            if (line.StartsWith("fn @", StringComparison.Ordinal))
            {
                if (result.Functions.Count >= RustSharpMetadataDocument.MaximumFunctions)
                    throw new InvalidDataException("Source package MIR function count exceeds its bound.");
                int nameStart = line.IndexOf(' ', 4) + 1;
                int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
                int entry = line.IndexOf(" entry bb", StringComparison.Ordinal);
                if (nameStart <= 0 || arrow < nameStart || entry <= arrow)
                    throw new InvalidDataException("Malformed source package MIR function evidence.");
                function = new(line[nameStart..arrow], line[(arrow + 4)..entry], [], []);
                result.Functions.Add(function);
                layout = null;
                continue;
            }
            if (function is not null && line.StartsWith("  return_variant ", StringComparison.Ordinal))
            {
                if (function.ReturnVariants.Count >= RustSharpMetadataDocument.MaximumCallTermsPerFunction)
                    throw new InvalidDataException("Source MIR returned variant count exceeds its bound.");
                function.ReturnVariants.Add(ParseSourceMirReturnedVariant(line["  return_variant ".Length..]));
                continue;
            }
            if (line.StartsWith("adt ", StringComparison.Ordinal))
            {
                if (result.Layouts.Count >= RustSharpMetadataDocument.MaximumValueTypes)
                    throw new InvalidDataException("Source package MIR layout count exceeds its bound.");
                int copy = line.IndexOf(" copy ", StringComparison.Ordinal);
                int move = line.IndexOf(" move ", StringComparison.Ordinal);
                int end = copy >= 0 ? copy : move;
                if (end < 4) throw new InvalidDataException("Malformed source package MIR layout evidence.");
                layout = new(line[4..end], copy >= 0, []);
                result.Layouts.Add(layout);
                function = null;
                continue;
            }
            if (function is not null && line.StartsWith("  let %", StringComparison.Ordinal))
            {
                int kindStart = line.IndexOf(' ', 7) + 1;
                if (kindStart <= 0 || !line.AsSpan(kindStart).StartsWith("parameter ", StringComparison.Ordinal)) continue;
                if (function.ParameterTypes.Count >= RustSharpMetadataDocument.MaximumCallTermsPerFunction)
                    throw new InvalidDataException("Source package MIR parameter count exceeds its bound.");
                int typeStart = line.IndexOf(": ", kindStart, StringComparison.Ordinal) + 2;
                int sourceStart = line.LastIndexOf(" [", StringComparison.Ordinal);
                if (typeStart <= 1 || sourceStart < typeStart)
                    throw new InvalidDataException("Malformed source package MIR parameter type evidence.");
                string type = line[typeStart..sourceStart];
                function.ParameterStaticLifetimes.Add(type.Contains(" static_lifetime", StringComparison.Ordinal));
                int flag = type.IndexOf(" drop=@", StringComparison.Ordinal);
                if (flag >= 0) type = type[..flag];
                foreach (string marker in new[] { " unit_adt", " promoted_constant", " static_lifetime" })
                {
                    if (type.EndsWith(marker, StringComparison.Ordinal)) type = type[..^marker.Length];
                }
                function.ParameterTypes.Add(type);
                continue;
            }
            if (layout is not null && line.StartsWith("  field ", StringComparison.Ordinal))
            {
                if (layout.Fields.Count >= RustSharpMetadataDocument.MaximumFieldsPerValueType)
                    throw new InvalidDataException("Source package MIR source field count exceeds its bound.");
                int nameStart = line.IndexOf(' ', 8) + 1;
                int colon = line.IndexOf(": ", nameStart, StringComparison.Ordinal);
                int sourceStart = line.LastIndexOf(" [", StringComparison.Ordinal);
                if (nameStart <= 0 || colon < nameStart || sourceStart < colon + 2)
                    throw new InvalidDataException("Malformed source package MIR source field evidence.");
                string type = line[(colon + 2)..sourceStart];
                bool isStatic = type.EndsWith(" static_refs", StringComparison.Ordinal);
                if (isStatic)
                {
                    type = type[..^" static_refs".Length];
                }
                layout.Fields.Add(new(line[nameStart..colon], type) { RequiresStaticLifetime = isStatic });
                continue;
            }
            if (layout is not null && line.StartsWith("  variant ", StringComparison.Ordinal))
            {
                if (layout.Variants.Count >= RustSharpMetadataDocument.MaximumFieldsPerValueType)
                    throw new InvalidDataException("Source MIR variant count exceeds its bound.");
                int nameStart = line.IndexOf(' ', 10) + 1;
                int tag = line.IndexOf(" tag=", StringComparison.Ordinal);
                int offset = line.IndexOf(" offset=", StringComparison.Ordinal);
                int fieldCount = line.IndexOf(" fields=", StringComparison.Ordinal);
                int source = line.LastIndexOf(" [", StringComparison.Ordinal);
                if (nameStart <= 0 || tag < nameStart || offset < tag || fieldCount < offset || source < fieldCount)
                    throw new InvalidDataException("Malformed source MIR enum variant evidence.");
                layout.Variants.Add(new(line[nameStart..tag],
                    int.Parse(line.AsSpan(tag + 5, offset - tag - 5), CultureInfo.InvariantCulture),
                    int.Parse(line.AsSpan(offset + 8, fieldCount - offset - 8), CultureInfo.InvariantCulture),
                    int.Parse(line.AsSpan(fieldCount + 8, source - fieldCount - 8), CultureInfo.InvariantCulture)));
            }
            if (line == "}") { function = null; layout = null; }
        }
        return result;
    }

    private static RustSharpMetadataSourceReturnVariant ParseSourceMirReturnedVariant(string text)
    {
        if (text.Length > SafeCoreSourceOriginCodec.MaximumCharacters)
            throw new InvalidDataException("Source MIR returned variant exceeds its character bound.");
        RustSharpMetadataReader.ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(text));
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("valuePath", out JsonElement path) || path.ValueKind != JsonValueKind.Array ||
            path.GetArrayLength() > SafeCoreSourceOriginCodec.MaximumDepth ||
            !root.TryGetProperty("variantName", out JsonElement name) || name.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Source MIR returned variant has incomplete or unknown terms.");
        string[] encoded = path.EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("Source MIR returned variant paths require explicit strings.")).ToArray();
        string variantName = name.GetString()!;
        string canonical = SafeCoreSourceOriginCodec.FormatReturnedVariant(encoded.Select(static value =>
            SafeCoreSourceOriginCodec.ParseProjection(value)).ToArray(), variantName);
        if (canonical != text) throw new InvalidDataException("Source MIR returned variant evidence is not canonical.");
        return new(encoded, variantName);
    }

    private static void ValidateOwnershipContracts(
        RustSharpMetadataDocument document,
        List<string> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RustSharpMetadataOwnershipFunction ownership in document.Ownership)
        {
            RustSharpMetadataFunction? function = FindFunction(document, ownership.FunctionId);
            if (function is not null && !seen.Add(function.Name))
            {
                AddContractDiagnostic(diagnostics,
                    "ownership contract function ID '" + Trim(ownership.FunctionId) + "' is duplicated.");
                continue;
            }

            if (function is null)
            {
                AddContractDiagnostic(diagnostics,
                    "ownership contract references unknown function '" + Trim(ownership.FunctionId) + "'.");
            }

            if (!string.Equals(ownership.PanicStrategy, "unwind", StringComparison.Ordinal) &&
                !string.Equals(ownership.PanicStrategy, "abort", StringComparison.Ordinal))
            {
                AddContractDiagnostic(diagnostics,
                    "ownership contract for '" + Trim(ownership.FunctionId) + "' has an unsupported panic strategy.");
            }
        }
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

    /// <summary>
    /// Correlates nominal layout evidence with the generated ValueType and
    /// Field tables. This prevents a stale producer JSON document from
    /// describing a different aggregate shape than its MemberRefs expose.
    /// </summary>
    private static void ValidateGeneratedValueTypes(
        MetadataReader metadata,
        RustSharpMetadataDocument document,
        List<string> diagnostics)
    {
        const string generatedNamespace = "RustSharp.Generated.Values";
        var actual = new Dictionary<string, (string Name, string Type)[]>(StringComparer.Ordinal);
        int typeRows = metadata.GetTableRowCount(TableIndex.TypeDef);
        int typeLimit = Math.Min(typeRows, RustSharpMetadataDocument.MaximumValueTypes + 2);
        int generatedCount = 0;
        for (int row = 1; row <= typeLimit; row++)
        {
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(row);
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            if (!string.Equals(metadata.GetString(definition.Namespace), generatedNamespace, StringComparison.Ordinal))
                continue;
            generatedCount++;
            string typeName = metadata.GetString(definition.Name);
            if (actual.ContainsKey(typeName))
            {
                AddContractDiagnostic(diagnostics,
                    "generated value type '" + Trim(typeName) + "' is duplicated.");
                continue;
            }

            var fields = new List<(string Name, string Type)>();
            int fieldCount = 0;
            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                if (++fieldCount > RustSharpMetadataDocument.MaximumFieldsPerValueType)
                {
                    AddContractDiagnostic(diagnostics,
                        "generated value type '" + Trim(typeName) + "' exceeds its field bound.");
                    break;
                }
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                string fieldName = metadata.GetString(field.Name);
                string? error = null;
                if ((field.Attributes & FieldAttributes.Static) != 0 ||
                    !TryDecodeGeneratedField(metadata, field, out string fieldType, out error))
                {
                    AddContractDiagnostic(diagnostics,
                        "generated value field '" + Trim(fieldName) + "' has an invalid shape: " + Trim(error));
                    continue;
                }
                fields.Add((fieldName, fieldType));
            }
            actual[typeName] = fields.ToArray();
        }

        if (typeRows > typeLimit)
            AddContractDiagnostic(diagnostics, "generated value type table exceeds its bounded correlation limit.");
        if (generatedCount != document.ValueTypes.Length)
            AddContractDiagnostic(diagnostics,
                "metadata value layout count " + document.ValueTypes.Length.ToString(CultureInfo.InvariantCulture) +
                " does not match generated value type count " + generatedCount.ToString(CultureInfo.InvariantCulture) + ".");

        foreach (RustSharpMetadataValueType declared in document.ValueTypes)
        {
            if (!actual.TryGetValue(declared.Name, out (string Name, string Type)[]? fields))
            {
                AddContractDiagnostic(diagnostics,
                    "metadata value layout '" + Trim(declared.Name) + "' has no generated value type.");
                continue;
            }
            RustSharpMetadataField[] expected = declared.Fields?.ToArray() ?? [];
            if (expected.Length != fields.Length)
            {
                AddContractDiagnostic(diagnostics,
                    "metadata value layout '" + Trim(declared.Name) + "' field count does not match generated fields.");
                continue;
            }
            for (int index = 0; index < expected.Length; index++)
            {
                if (!string.Equals(expected[index].Name, fields[index].Name, StringComparison.Ordinal) ||
                    !string.Equals(expected[index].Type, fields[index].Type, StringComparison.Ordinal))
                {
                    AddContractDiagnostic(diagnostics,
                        "metadata value layout '" + Trim(declared.Name) + "' field order or type does not match the generated Field table.");
                    break;
                }
            }
        }
    }

    private static bool TryDecodeGeneratedField(
        MetadataReader metadata,
        FieldDefinition field,
        out string type,
        out string? error)
    {
        type = string.Empty;
        error = null;
        try
        {
            type = field.DecodeSignature(GeneratedSignatureTypeProvider.Instance, genericContext: null);
            return !string.IsNullOrWhiteSpace(type);
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
            byte rawTypeKind)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            if (rawTypeKind != (byte)SignatureTypeKind.ValueType ||
                reader.GetString(reference.Namespace) != "RustSharp.Generated.Values" ||
                reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
                throw Unsupported("an unsupported external type reference");
            AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
            string assemblyName = reader.GetString(assembly.Name);
            string typeName = reader.GetString(reference.Name);
            if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(typeName) ||
                assemblyName.Length > 1024 || typeName.Length > 1024 || assemblyName.Contains("::", StringComparison.Ordinal))
                throw Unsupported("an invalid external value identity");
            return "Value(" + assemblyName + "::" + typeName + ")";
        }

        public string GetSZArrayType(string elementType) => throw Unsupported("an SZ array");

        public string GetArrayType(string elementType, ArrayShape shape) => throw Unsupported("an array");

        public string GetByReferenceType(string elementType)
        {
            if (string.IsNullOrWhiteSpace(elementType) || elementType.Length > 1024)
                throw Unsupported("an invalid by-reference element");
            return "&" + elementType;
        }

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
        bool found = false;
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!string.Equals(metadata.GetString(resource.Name), "RustSharp.Generics.v1.json", StringComparison.Ordinal))
                continue;
            if (found)
            {
                diagnostics.Add(InvalidMetadata + ": generic metadata resource is duplicated.");
                return null;
            }
            found = true;
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
